using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Parse;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// PdfPig-based engine for digital PDFs (#301). Owns the full <c>.pdf</c> parsing pass so it
/// can extract the text layer (words + bbox + reading order) and the embedded raster image objects, then
/// transcribe each image through the host-selected <see cref="ParseOcrClient"/> and inline the
/// transcription at the image's reading position. Each page with content is a section of the document,
/// referring to its page (PdfPig's frame: points, origin bottom-left). This closes the silent-image-loss gap
/// where embedded figures in digital PDFs never reached the channel output.
/// <para>
/// <b>Image → text uses <see cref="ParseOcrClient"/> only</b> (no keyed Vision <c>IChatClient</c> here, no
/// new LLM call site). Semantics are transcription only; the figure's bytes are the OCR input, so there is
/// no user free-text entering a prompt and no <c>PromptBoundary</c> concern.
/// </para>
/// <para>
/// <b>Scope.</b> A PDF with no digital text layer (scanned / image-only) returns an empty document here and
/// is left to the router's whole-page OCR fallback — this provider does not OCR images on
/// a text-less PDF, so there is no double OCR. Vector-only graphics are an accepted blind spot
/// (<c>GetImages()</c> does not see them).
/// </para>
/// </summary>
[ExposeServices(typeof(ParseExtractionClient), typeof(PdfExtractor))]
public class PdfExtractor : ParseExtractionClient, ITransientDependency
{
    /// <summary>The engine name written to <see cref="ParseDocumentMetadata.Provider"/>.</summary>
    public const string ProviderIdentifier = "PdfPig";

    private readonly ParseOcrClient _ocr;
    private readonly PdfExtractorOptions _options;

    public ILogger<PdfExtractor> Logger { get; set; } = NullLogger<PdfExtractor>.Instance;

    public PdfExtractor(
        ParseOcrClient ocr,
        IOptions<PdfExtractorOptions> options)
    {
        _ocr = ocr;
        _options = options.Value;
    }

    public override string ProviderName => ProviderIdentifier;

    public override bool CanExtract(string fileExtension, string mediaType)
        => fileExtension == ".pdf" || (fileExtension.Length == 0 && string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase));

    public override async Task<ExtractionDocument> ExtractAsync(
        Stream fileStream,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ParseStreams.ReadAllBytesAsync(fileStream, cancellationToken);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Corrupt / encrypted / not-actually-a-PDF: return an empty document so the router's whole-page OCR
            // fallback (which fires when a PDF yields no meaningful text) can try instead.
            Logger.LogWarning(ex, "PdfPig could not open the PDF ({Bytes} bytes); deferring to OCR fallback.", bytes.Length);
            return Empty(identifier);
        }

        using (document)
        {
            var pages = new List<PageContent>();
            var failedPages = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<Word> words;
                try
                {
                    words = page.GetWords().ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // PdfPig parses content streams lazily, so a corrupt page / malformed font faults here (on access),
                    // not at Open. The page is read by OCR like a scanned one; without OCR it is skipped and the result
                    // marked incomplete rather than failing the whole document (#268).
                    Logger.LogWarning(ex, "Failed to parse the text layer of a PDF page; reading it by OCR.");
                    pages.Add(new PageContent(page, Array.Empty<Word>(), PdfPageTextQuality.Missing, TextLayerFailed: true));
                    continue;
                }

                // A page with no text, or text that does not say what the page shows (a broken font encoding, scan
                // noise), is read by OCR instead.
                var quality = PdfPageText.Judge(words);
                pages.Add(new PageContent(page, words, quality));
            }

            // No usable text layer anywhere → a scanned / image-only (or wholly garbled) PDF. Return empty so the
            // router's OCR fallback reads the whole file in one call; no page is OCR'd here.
            if (pages.All(p => p.Quality != PdfPageTextQuality.Usable))
            {
                Logger.LogDebug("PDF has no usable digital text layer; deferring to whole-document OCR.");
                return Empty(identifier);
            }

            var extracted = new ExtractionDocument(identifier).SetProvider(ProviderIdentifier);
            var readPages = new List<(Page Page, PdfTableContinuation.Page Blocks)>(pages.Count);
            var imageBudget = _options.MaxImagesPerPdf;
            var droppedByCap = 0;
            var undecodable = 0;
            var truncatedOcr = 0;
            var failedFigureOcr = 0;
            // #268 (#450 lattice): meaningful table fragments dropped as outside-the-drawn-grid, across all pages.
            var latticeDroppedFragments = 0;
            // Distinct from failedPages (GetWords fault → whole page dropped): here GetImages faulted but
            // the page's text was already extracted and is retained — only its images are skipped.
            var pagesWithSkippedImages = 0;
            // Pages without a usable text layer, read by OCR one at a time: failed, truncated, or past the cap.
            var ocrPageBudget = _options.MaxOcrPagesPerPdf;
            var failedPageOcr = 0;
            var truncatedPageOcr = 0;
            var pagesOverOcrCap = 0;

            var languageHints = ResolveLanguageHints(options);

            // #383: detect running headers / footers + page numbers (content that repeats in the top/bottom
            // edge band across pages) once over all pages, so it does not pollute the Markdown body. Position
            // only selects candidates; a line is dropped only when its digit-normalized text repeats across
            // enough pages — so a per-page signature / seal / total / footnote is preserved, and a single-page
            // document drops nothing.
            var runningContent = PdfRunningHeaderFooter.Detect(pages.ConvertAll(p => p.Quality == PdfPageTextQuality.Usable ? p.Words : Array.Empty<Word>()));

            // #403: build the document-wide font-size → heading-level scale once (rank of the distinct sizes
            // larger than the char-count-weighted body size), then pass it to each page render so larger/bold
            // lines become Markdown headings. Built from all words; a single-size document yields no headings.
            var headingScale = PdfHeadingScale.Build(pages.Where(p => p.Quality == PdfPageTextQuality.Usable).SelectMany(p => p.Words));

            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageContent = pages[pageIndex];
                if (pageContent.Quality != PdfPageTextQuality.Usable)
                {
                    if (!_options.OcrPagesWithoutText)
                    {
                        // Without page OCR a page whose text layer could not be parsed is lost; the others read on
                        // below as before (a scanned page's raster as a figure, a garbled page's text as written).
                        if (pageContent.TextLayerFailed)
                        {
                            failedPages++;
                            continue;
                        }
                    }
                    else
                    {
                        if (ocrPageBudget <= 0)
                        {
                            pagesOverOcrCap++;
                            continue;
                        }

                        ocrPageBudget--;
                        var read = await OcrPageAsync(document, pageContent.Page.Number, identifier, languageHints, cancellationToken);
                        if (read is null)
                        {
                            failedPageOcr++;
                            continue;
                        }

                        if (!read.IsComplete())
                        {
                            truncatedPageOcr++;
                        }

                        var elements = read.Sections.SelectMany(section => section.Elements).ToList();
                        if (elements.Count > 0)
                        {
                            readPages.Add((pageContent.Page, new PdfTableContinuation.Page(
                                pageContent.Page.Number,
                                [new PdfElementsBlock(new PdfRectangle(0, 0, pageContent.Page.Width, pageContent.Page.Height), elements)])));
                        }

                        continue;
                    }
                }

                var figures = new List<PdfReadingOrder.Figure>();

                List<IPdfImage> images;
                try
                {
                    images = pageContent.Page.GetImages().ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lazy content-stream parse can fault on a corrupt page object / font. Keep the page's
                    // already-extracted text, skip its images, and mark the result incomplete (#268) rather
                    // than failing the whole document.
                    Logger.LogWarning(ex, "Failed to read images on a PDF page; keeping its text, skipping its images.");
                    pagesWithSkippedImages++;
                    images = new List<IPdfImage>();
                }

                foreach (var image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsTooSmall(image))
                    {
                        // Decorative icon / bullet / rule / spacer — not figure content, not counted incomplete.
                        continue;
                    }

                    if (IsFullPageScanBackground(pageContent.Page, image, pageContent.Words))
                    {
                        // Searchable / "sandwich" PDF (#309): this image is the full-page scan raster and the
                        // page's text layer is its OCR transcription. Re-OCRing it would duplicate text already
                        // extracted and burn a redundant (paid) vision call. Skip it WITHOUT OCR, WITHOUT
                        // emitting a figure, and WITHOUT tripping the completeness signal — an intentional,
                        // non-lossy skip (the text layer is retained), not a dropped figure. The guard is
                        // high-precision and errs toward keeping, so a real full-page figure is never dropped.
                        continue;
                    }

                    if (imageBudget <= 0)
                    {
                        droppedByCap++;
                        continue;
                    }

                    (byte[] Bytes, string ContentType)? payload;
                    try
                    {
                        payload = PdfImagePayload.TryResolve(image);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.LogWarning(ex, "Failed to decode an embedded PDF image; skipping it.");
                        payload = null;
                    }

                    if (payload is null)
                    {
                        // Unsupported codec (JBIG2 / JPX / CCITT) or undecodable raw bitmap.
                        undecodable++;
                        continue;
                    }

                    imageBudget--;

                    ExtractionDocument ocr;
                    try
                    {
                        using var imageStream = new MemoryStream(payload.Value.Bytes, writable: false);
                        ocr = await _ocr.ExtractAsync(
                            imageStream,
                            identifier + "#figure",
                            payload.Value.ContentType,
                            ParseExtractionOptions.Create(fileName: null, languageHints),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // A single figure's OCR failing (provider timeout / rate-limit / auth / one bad image)
                        // must NOT discard the digital text layer already extracted — figure OCR is an
                        // auxiliary augmentation here, not the document's primary payload (the #210/#268
                        // "an auxiliary step must not break the main pipeline" principle). Skip this figure
                        // and mark the result incomplete. A cancellation of the caller's token still
                        // propagates so a host/job shutdown aborts promptly; an OperationCanceledException
                        // raised while that token is not cancelled is a provider timeout (HttpClient surfaces
                        // it as TaskCanceledException) and is a figure failure like any other.
                        Logger.LogWarning(ex, "Embedded-image OCR failed; keeping the text layer, skipping this figure.");
                        failedFigureOcr++;
                        continue;
                    }

                    if (!ocr.IsComplete())
                    {
                        // OCR truncated at the token limit or discarded by the repetition guard.
                        truncatedOcr++;
                    }

                    var transcription = ExtractionDocumentMarkdown.Render(ocr).Trim();
                    if (transcription.Length > 0)
                    {
                        // Inline output (#301): woven into the page Markdown at the reading position, bracketed by
                        // *[Image OCR p:N]*…*[End OCR]* provenance markers (#371/#381) carrying the page anchor. The
                        // figure span — its transcription + page — travels in Document.Markdown (the markers stay in
                        // the egress); the unified sub-document detection pass routes it from there.
                        figures.Add(new PdfReadingOrder.Figure(
                            image.BoundingBox, transcription, pageContent.Page.Number));
                    }
                }

                // #383: drop the words of any running header/footer / page-number lines detected across pages,
                // then render. Filter the page's own word list by reference identity (the drop set holds the
                // same Word instances).
                var droppable = runningContent[pageIndex];
                var renderWords = droppable.Count == 0
                    ? pageContent.Words
                    : pageContent.Words.Where(w => !droppable.Contains(w)).ToList();

                // #450 lattice: the page's vector ruling-line bounds (a table's drawn grid is the authoritative
                // column/row model). Guarded — a page whose content stream faults on path access still renders
                // its text; lattice detection is simply skipped for it (the stream table path still applies).
                IReadOnlyList<PdfRectangle> rulingBounds;
                try
                {
                    // Only what is drawn: a clipping path (the text frame's clip rectangle, say) is not a rule.
                    rulingBounds = pageContent.Page.Paths
                        .Where(path => !path.IsClipping && (path.IsStroked || path.IsFilled))
                        .SelectMany(path => path)
                        .Select(subpath => subpath.GetBoundingRectangle())
                        .Where(rect => rect.HasValue)
                        .Select(rect => rect!.Value)
                        .ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(ex, "Failed to read vector paths on a PDF page; table lattice detection skipped for it.");
                    rulingBounds = Array.Empty<PdfRectangle>();
                }

                var pageBlocks = PdfReadingOrder.ReadPage(
                    renderWords, figures, _options.ReconstructTables, headingScale, rulingBounds,
                    out var pageLatticeDropped);
                latticeDroppedFragments += pageLatticeDropped;
                if (pageBlocks.Count > 0)
                {
                    readPages.Add((pageContent.Page, new PdfTableContinuation.Page(pageContent.Page.Number, pageBlocks)));
                }
            }

            // A table a page break cut in two reads as one, on the page it starts on.
            PdfTableContinuation.Join(readPages.ConvertAll(read => read.Blocks));
            AddPages(extracted, readPages);

            // Single source of truth for the #268 signal: the reason string is built from the counters and
            // is null iff nothing was lost; completeness derives from it (no parallel hand-synced predicate
            // that could drift when a new counter is added).
            var incompleteReason = BuildIncompleteReason(
                failedPages, pagesWithSkippedImages, droppedByCap, undecodable, truncatedOcr, failedFigureOcr,
                latticeDroppedFragments, failedPageOcr, truncatedPageOcr, pagesOverOcrCap);
            var complete = incompleteReason is null;
            if (!complete)
            {
                Logger.LogWarning("PDF extraction incomplete: {Reason}", incompleteReason);
            }

            return complete ? extracted : extracted.MarkIncomplete(incompleteReason!);
        }
    }

    /// <summary>Whether an embedded image is below the <see cref="PdfExtractorOptions.MinImagePixels"/> threshold (decorative).</summary>
    protected virtual bool IsTooSmall(IPdfImage image)
    {
        var pixels = (long)image.WidthInSamples * image.HeightInSamples;
        return pixels < _options.MinImagePixels;
    }

    /// <summary>
    /// High-precision guard for the searchable / "sandwich" PDF case (#309): a full-page scan raster whose
    /// content is already present as the page's (often invisible) OCR text layer. Returns <c>true</c> only
    /// on a strong redundancy signal so a real full-page figure (figure + caption / infographic / photo) is
    /// never dropped — <b>when unsure it returns <c>false</c></b> (keep + OCR), because dropping a real
    /// figure is lossy and unacceptable whereas an occasional missed skip is merely a wasted OCR call.
    /// <para>Two signals must BOTH hold:</para>
    /// <list type="number">
    /// <item><b>Full-page placement.</b> The image's placement bbox (<c>image.BoundingBox</c>, NOT pixel
    /// <c>WidthInSamples × HeightInSamples</c>) covers at least
    /// <see cref="PdfExtractorOptions.FullPageScanCoverageThreshold"/> of the page box on both width and
    /// height.</item>
    /// <item><b>Redundant text layer.</b> The page's text over the image region reads as a whole-page
    /// transcription: it spans at least <see cref="PdfExtractorOptions.FullPageScanMinTextVerticalCoverage"/>
    /// of the image height (rejecting a thin edge caption band — the primary anti-false-positive guard) AND
    /// it is either many lines (<see cref="PdfExtractorOptions.FullPageScanMinTextLines"/>) or a
    /// predominantly invisible <c>Tr 3</c> layer (<see cref="PdfExtractorOptions.FullPageScanMinInvisibleTextRatio"/>
    /// — the canonical sandwich signature; a bonus that is sufficient but not necessary).</item>
    /// </list>
    /// Out of scope (left to double-OCR for now, #309): tiled multi-image scan pages, heavily
    /// margined/cropped scans whose single image does not clear the coverage bar, and rotated placements
    /// (PdfPig's PdfRectangle edge accessors are not rotation-aware, so the geometry math no-ops). All fail
    /// safe — they re-OCR rather than ever dropping a figure.
    /// </summary>
    protected virtual bool IsFullPageScanBackground(Page page, IPdfImage image, IReadOnlyList<Word> words)
    {
        if (!_options.SkipFullPageScanBackground)
        {
            return false;
        }

        var imageBox = image.BoundingBox;
        var imageWidth = Math.Abs(imageBox.Width);
        var imageHeight = Math.Abs(imageBox.Height);
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return false;
        }

        // --- Signal 1: the image's placement bbox ≈ the whole page ---
        // Prefer the visible CropBox; fall back to the MediaBox when it is degenerate.
        var pageBox = page.CropBox.Bounds;
        if (Math.Abs(pageBox.Width) <= 0 || Math.Abs(pageBox.Height) <= 0)
        {
            pageBox = page.MediaBox.Bounds;
        }

        var pageWidth = Math.Abs(pageBox.Width);
        var pageHeight = Math.Abs(pageBox.Height);
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return false;
        }

        var widthCoverage = imageWidth / pageWidth;
        var heightCoverage = imageHeight / pageHeight;
        if (widthCoverage < _options.FullPageScanCoverageThreshold ||
            heightCoverage < _options.FullPageScanCoverageThreshold)
        {
            return false;
        }

        // --- Signal 2: the text layer densely covers the image region ---
        // After signal 1 the image ≈ the page, but scope to the image region so the test stays correct when
        // the image is only ~the threshold size. A bare full-page figure/photo has no text here → kept
        // (this is exactly the content #301 exists to capture).
        var regionWords = words.Where(w => CentroidInside(w.BoundingBox, imageBox)).ToList();
        if (regionWords.Count == 0)
        {
            return false;
        }

        // Re-group the in-region words into lines here rather than reusing the per-page `lines` already built
        // in ExtractAsync: passing that list in would force this `protected virtual` method (kept overridable
        // for module extensibility) to expose the internal PdfReadingOrder.TextLine type (CS0051). The
        // re-group runs only for full-page-sized images (≈one per sandwich page) and is cheap relative to the
        // OCR/IO it gates, so the duplicated clustering is an accepted trade-off.
        var regionLines = PdfReadingOrder.GroupWordsIntoLines(regionWords);
        if (regionLines.Count == 0)
        {
            return false;
        }

        // Vertical span of the text block relative to the image height — the PRIMARY guard against dropping
        // a full-page figure whose only text is an edge caption (a caption band spans little of the height).
        var topMost = regionLines.Max(l => l.Bounds.Top);
        var bottomMost = regionLines.Min(l => l.Bounds.Bottom);
        var verticalCoverage = (topMost - bottomMost) / imageHeight;
        if (verticalCoverage < _options.FullPageScanMinTextVerticalCoverage)
        {
            return false;
        }

        // A whole-page transcription is many distributed lines. The invisible-text (Tr 3) ratio is the
        // canonical sandwich signature — sufficient on its own (an OCR layer is invisible by construction)
        // but NOT required, so a visible-but-dense layer still qualifies on line count alone.
        var hasManyLines = regionLines.Count >= _options.FullPageScanMinTextLines;
        var invisibleRatio = InvisibleTextRatio(regionWords);
        var looksInvisibleSandwich = invisibleRatio >= _options.FullPageScanMinInvisibleTextRatio;
        if (!hasManyLines && !looksInvisibleSandwich)
        {
            return false;
        }

        Logger.LogDebug(
            "Skipping a full-page scan background on page {Page}: image covers {Width:P0}×{Height:P0} of the " +
            "page, text spans {Vertical:P0} of the image over {Lines} line(s), invisible-text ratio {Invisible:P0}.",
            page.Number, widthCoverage, heightCoverage, verticalCoverage, regionLines.Count, invisibleRatio);

        return true;
    }

    /// <summary>Whether <paramref name="inner"/>'s centroid falls inside <paramref name="outer"/> (robust to flipped rectangles).</summary>
    private static bool CentroidInside(PdfRectangle inner, PdfRectangle outer)
    {
        var centroid = inner.Centroid;
        var left = Math.Min(outer.Left, outer.Right);
        var right = Math.Max(outer.Left, outer.Right);
        var bottom = Math.Min(outer.Bottom, outer.Top);
        var top = Math.Max(outer.Bottom, outer.Top);
        return centroid.X >= left && centroid.X <= right && centroid.Y >= bottom && centroid.Y <= top;
    }

    /// <summary>
    /// Fraction of letters drawn in the invisible text-rendering mode (<see cref="TextRenderingMode.Neither"/>,
    /// PDF "Tr 3") across the given words — the canonical signature of a scan-sandwich OCR layer. Returns 0
    /// when there are no letters.
    /// </summary>
    private static double InvisibleTextRatio(IReadOnlyList<Word> words)
    {
        long total = 0;
        long invisible = 0;
        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                total++;
                if (letter.RenderingMode == TextRenderingMode.Neither)
                {
                    invisible++;
                }
            }
        }

        return total == 0 ? 0.0 : (double)invisible / total;
    }

    private static ExtractionDocument Empty(string identifier) => new ExtractionDocument(identifier).SetProvider(ProviderIdentifier);

    /// <summary>
    /// A source page for every page read, and a section for every page that still holds blocks, its elements each
    /// referring to their region of the page in PdfPig's frame (points, origin bottom-left). A page a table's continuation
    /// filled has no section of its own; the table refers to it.
    /// </summary>
    private static void AddPages(ExtractionDocument document, IReadOnlyList<(Page Page, PdfTableContinuation.Page Blocks)> pages)
    {
        var sources = new Dictionary<int, ExtractionDocumentSourcePage>();
        foreach (var (page, _) in pages)
        {
            var source = new ExtractionDocumentSourcePage(
                "page-" + page.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                page.Number,
                page.Width > 0 && page.Height > 0 ? new ExtractionDocumentPageDimensions(page.Width, page.Height) : null,
                ExtractionDocumentCoordinateUnit.Point,
                ExtractionDocumentCoordinateOrigin.BottomLeft);
            document.SourcePages.Add(source);
            sources[page.Number] = source;
        }

        foreach (var (page, read) in pages.Where(read => read.Blocks.Blocks.Count > 0))
        {
            var section = new ExtractionDocumentSection();
            section.Sources.Add(new ExtractionDocumentSourceReference(sources[page.Number]));
            document.Sections.Add(section);
            section.Elements.AddRange(PdfElements.Create(read.Blocks, sources[page.Number], number => sources.GetValueOrDefault(number)));
        }
    }

    /// <summary>
    /// Resolves the OCR language hints for embedded-image transcription: the per-document hints from the
    /// extraction options, or empty. There is no central host default (#441 removed it); a provider that needs a
    /// language default reads its own config (e.g. PaddleOcr:Languages). Kept <c>protected virtual</c> so a
    /// consumer can override to supply hints (e.g. from per-tenant config).
    /// </summary>
    protected virtual IReadOnlyList<string> ResolveLanguageHints(DocumentExtractionOptions? options)
        => options.GetLanguageHints();

    /// <summary>
    /// Builds the #268 incompleteness reason from the loss counters, or returns <c>null</c> when nothing
    /// was lost. This is the single source of truth for both the reason text and completeness
    /// (<c>IsComplete = reason is null</c>), so a new counter cannot drift out of sync with a separate
    /// boolean predicate.
    /// </summary>
    internal static string? BuildIncompleteReason(
        int failedPages, int pagesWithSkippedImages, int droppedByCap, int undecodable, int truncatedOcr, int failedFigureOcr,
        int latticeDroppedFragments, int failedPageOcr = 0, int truncatedPageOcr = 0, int pagesOverOcrCap = 0)
    {
        var parts = new List<string>();
        if (failedPages > 0)
        {
            parts.Add($"{failedPages} page(s) could not be parsed and were skipped");
        }

        if (failedPageOcr > 0)
        {
            parts.Add($"{failedPageOcr} page(s) without a usable text layer failed OCR and were skipped");
        }

        if (truncatedPageOcr > 0)
        {
            parts.Add($"{truncatedPageOcr} page transcription(s) were truncated or discarded by the OCR provider");
        }

        if (pagesOverOcrCap > 0)
        {
            parts.Add($"{pagesOverOcrCap} page(s) without a usable text layer were skipped after reaching the per-document OCR page cap");
        }

        if (pagesWithSkippedImages > 0)
        {
            parts.Add($"{pagesWithSkippedImages} page(s) had unreadable images skipped (page text retained)");
        }

        if (undecodable > 0)
        {
            parts.Add($"{undecodable} embedded image(s) could not be decoded to a supported image format");
        }

        if (failedFigureOcr > 0)
        {
            parts.Add($"{failedFigureOcr} embedded image(s) failed OCR (provider error)");
        }

        if (truncatedOcr > 0)
        {
            parts.Add($"{truncatedOcr} image transcription(s) were truncated or discarded by the OCR provider");
        }

        if (droppedByCap > 0)
        {
            parts.Add($"{droppedByCap} image(s) were skipped after reaching the per-document image cap");
        }

        if (latticeDroppedFragments > 0)
        {
            parts.Add($"{latticeDroppedFragments} table fragment(s) fell outside a drawn table grid and were dropped");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private readonly record struct PageContent(
        Page Page, IReadOnlyList<Word> Words, PdfPageTextQuality Quality, bool TextLayerFailed = false);

    /// <summary>
    /// Page <paramref name="pageNumber"/> read by the OCR engine, as a one-page PDF (PdfPig copies the page), or
    /// <c>null</c> when it could not be copied or read. A cancellation of the caller's token still propagates.
    /// </summary>
    protected virtual async Task<ExtractionDocument?> OcrPageAsync(
        PdfDocument document, int pageNumber, string identifier, IReadOnlyList<string> languageHints, CancellationToken cancellationToken)
    {
        try
        {
            byte[] page;
            using (var builder = new PdfDocumentBuilder())
            {
                builder.AddPage(document, pageNumber);
                page = builder.Build();
            }

            using var stream = new MemoryStream(page, writable: false);
            return await _ocr.ExtractAsync(
                stream,
                identifier + "#page-" + pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "application/pdf",
                ParseExtractionOptions.Create(fileName: null, languageHints),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "OCR of PDF page {PageNumber} failed; skipping the page.", pageNumber);
            return null;
        }
    }
}
