namespace Dignite.Vault.Parse.Ocr.VisionLlm;

/// <summary>
/// Behaviour knobs for the vision-LLM OCR provider. Bound from the <c>VisionLlmOcr</c> configuration
/// section by <see cref="VaultParseVisionLlmOcrModule"/>. The vision model id / endpoint / key are NOT
/// here — those are host deployment concerns wired in <c>VaultExtractHostModule.ConfigureAI</c> as a keyed
/// <c>IChatClient</c> (see <see cref="VisionLlmOcrConsts.VisionChatClientKey"/>).
/// </summary>
public class VisionLlmOcrOptions
{
    /// <summary>
    /// Hard cap on tokens the model may generate per page/image. Bounds the worst-case cost AND the
    /// length of a runaway hallucination loop. A dense full page is typically &lt; ~2500 tokens; the
    /// 4096 default leaves headroom without letting a loop run unbounded.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature. OCR/transcription is deterministic by nature; 0 minimises both hallucination
    /// and the tendency to fall into repetition loops.
    /// </summary>
    public float Temperature { get; set; } = 0f;

    /// <summary>
    /// Repetition guard (heuristic 1): trip when a non-empty line appears this many times in a row (i.e.
    /// this many identical consecutive lines). Conservative — no legitimate receipt / document has this
    /// many identical consecutive lines. Values below 2 are treated as 2 (the strictest meaningful
    /// setting); to effectively disable this heuristic, set a very large value.
    /// </summary>
    public int MaxConsecutiveRepeatedLines { get; set; } = 24;

    /// <summary>
    /// Repetition guard (heuristic 2): when the output has at least <see cref="MinLinesForRatioCheck"/>
    /// non-empty lines and the ratio of distinct lines to total lines falls below this, the output is
    /// treated as a loop (catches interleaved A-B-A-B loops that heuristic 1 misses).
    /// </summary>
    public double MinDistinctLineRatio { get; set; } = 0.3;

    /// <summary>
    /// Minimum non-empty line count before the distinct-ratio heuristic applies. Below this, short
    /// outputs (notes, single-line receipts) are never flagged.
    /// </summary>
    public int MinLinesForRatioCheck { get; set; } = 40;

    /// <summary>
    /// Repetition guard (heuristic 3): minimum length of a single line before it is inspected for
    /// short-period repetition. Catches no-newline char-/phrase-level loops the line heuristics miss.
    /// Lines shorter than this (normal sentences, short dividers, dotted leaders) are never flagged.
    /// </summary>
    public int MinLengthForSegmentCheck { get; set; } = 200;

    /// <summary>
    /// Repetition guard (heuristic 3): largest repeating-unit (period) length treated as a loop. A line
    /// made of a unit no longer than this, tiled at least <see cref="MinRepeatedSegmentRepeats"/> times,
    /// is treated as a loop.
    /// </summary>
    public int MaxRepeatedSegmentLength { get; set; } = 120;

    /// <summary>
    /// Repetition guard (heuristic 3): minimum number of times the repeating unit must tile a line to
    /// trip the short-period check.
    /// </summary>
    public int MinRepeatedSegmentRepeats { get; set; } = 8;

    /// <summary>
    /// Maximum number of pages rasterized + transcribed for a scanned / image-only PDF. Each page is a
    /// separate paid vision-LLM call, so this bounds per-document cost and time. A PDF exceeding this
    /// fails loudly (the provider throws) rather than silently dropping pages.
    /// </summary>
    public int MaxPdfPages { get; set; } = 30;

    /// <summary>
    /// No-content-refusal guard: only a response no longer than this is checked against the refusal
    /// phrasings (e.g. "There is no readable text in the image to transcribe."). A refusal is always one
    /// short sentence, so this keeps the guard from ever matching a genuine (longer) transcription that
    /// happens to contain a word like "text".
    /// </summary>
    public int MaxNoContentRefusalLength { get; set; } = 160;

    /// <summary>
    /// The most pixels (width times height) of an image sent to the vision model; a larger one is scaled
    /// down first, keeping its aspect ratio. A large photo or screenshot can send the model into a repetition
    /// loop that discards the whole page (#692). Zero or less sends images as they are.
    /// </summary>
    public int MaxImagePixels { get; set; } = VisionLlmImageDownscaler.DefaultMaxPixels;

    /// <summary>
    /// The most pixels (width times height) an image may declare for this engine to decode it. Shrinking an image
    /// means decoding it, and a decoded bitmap's size follows its pixel count, not its file: a one-colour PNG of a few
    /// hundred kilobytes can declare gigabytes. An image over this that would have to be shrunk is refused from its
    /// header with an <see cref="System.IO.InvalidDataException"/>, which ends Vault Extract's parse run instead of
    /// retrying it; a PDF or Office engine that sent it as an embedded figure drops the figure and marks the document
    /// incomplete. A scanned PDF page is rendered at 300 dpi, or at the lower resolution that keeps it within this.
    /// The default is Pillow's decompression-bomb limit (<c>Image.MAX_IMAGE_PIXELS</c>, about 89.5 megapixels), as
    /// for <c>PdfExtractorOptions.MaxImagePixels</c>. Zero or less switches the cap off.
    /// </summary>
    public long MaxInputImagePixels { get; set; } = VisionLlmImageDownscaler.DefaultMaxInputPixels;
}
