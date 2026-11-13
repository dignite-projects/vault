using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Index.Ai;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dignite.Vault.Index.Compilation;

/// <summary>A chunk as compilation reads it.</summary>
public sealed record CompileChunk(int Ordinal, string? HeadingPath, string Text);

/// <summary>What one compilation produced and what it took.</summary>
public sealed class SummaryCompilation
{
    public required DocumentSummaries Summaries { get; init; }

    public long InputTokenCount { get; init; }

    public long OutputTokenCount { get; init; }

    public int CallCount { get; init; }

    public int FailedCallCount { get; init; }

    /// <summary>What was left out or could not be done; <c>null</c> when nothing.</summary>
    public string? Note { get; init; }

    /// <summary>Every model call failed: nothing was compiled, and trying again later may work.</summary>
    public bool Failed => CallCount > 0 && FailedCallCount == CallCount;
}

/// <summary>
/// Summarizes a document bottom-up along its outline (the same tree <c>outline</c> serves): every section gets one
/// sentence from its own text and its subsections' summaries, then the document gets a short summary from the text
/// before its first heading and its top sections' summaries. Sections of one depth go to the model in batches, deepest
/// first, so a parent is summarized after its children.
/// <para>
/// Bounds: a section's own text longer than <see cref="MaxTextLength"/> is split into parts that are summarized first
/// and stand in for it, so no text is dropped; a single chunk longer than that is cut, and the model is told. A
/// document with more than <see cref="MaxSections"/> sections is summarized only down to the depth that fits, the
/// deeper sections' text going into their parents'. A document that would need more than <see cref="MaxTasks"/>
/// summaries is not summarized. Each answer is cut at <see cref="MaxSectionSummaryLength"/> or
/// <see cref="MaxDocumentSummaryLength"/>.
/// </para>
/// <para>
/// Every document text is wrapped with <see cref="PromptBoundary"/> under its boundary rule; the instructions are
/// constants, and the language is named from <see cref="PromptLanguage"/>'s table. An answer that cannot be read is
/// asked for once more: in JSON schema mode a model can open the object and then emit only whitespace until its token
/// limit, which a second call usually does not repeat. Best effort beyond that: a batch whose call fails, or whose
/// answer cannot be read twice, leaves its sections without summaries, its parents are summarized from what there is,
/// and the compilation says so in its note.
/// </para>
/// </summary>
public class DocumentSummaryCompiler
{
    /// <summary>The most sections summarized on their own; deeper levels beyond it are folded into their parents.</summary>
    public const int MaxSections = 300;

    /// <summary>The most summaries (sections, parts and the document) one compilation writes.</summary>
    public const int MaxTasks = 1_000;

    /// <summary>The most of a section's own text, or of its subsection summaries, sent in one item.</summary>
    public const int MaxTextLength = 6_000;

    public const int ItemsPerCall = 8;

    /// <summary>The most text one call sends; an item larger than this goes alone.</summary>
    public const int MaxCallTextLength = 32_000;

    public const int MaxSectionSummaryLength = 300;

    public const int MaxDocumentSummaryLength = 1_200;

    /// <summary>Calls for one answer when the first cannot be read.</summary>
    public const int MaxAttempts = 2;

    private const string SectionInstructions =
        "You summarize the sections of a business document so that a reader can choose which section to read. For each "
        + "numbered item, return its index and one sentence of at most 150 characters that says what the item covers, "
        + "with its key names, amounts, dates and conditions. An item gives its heading, its own text, and the summaries "
        + "of its subsections or parts, in order; summarize them together. Do not begin with words like \"This section\".";

    private const string DocumentInstructions =
        "You summarize a business document. Return a summary of the whole document in two to four sentences, at most "
        + "600 characters: what kind of document it is, whom it concerns, and its key facts such as amounts, dates and "
        + "conditions. You are given its title, any text before its first heading, and the summaries of its top "
        + "sections or parts, in order.";

    private const string ContentInstruction =
        " Use only what the given text supports; for an item with no content, return an empty summary.";

    private const string CutNote = "(The text is cut here; it continues beyond what is shown.)";

    private const string MoreNote = "(More subsections follow that are not listed.)";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;
    private readonly string _languageInstruction;

    /// <param name="language">The document's language tag, such as <c>ja</c>; <c>null</c> when unknown.</param>
    public DocumentSummaryCompiler(IChatClient chatClient, ILogger logger, string? language = null)
    {
        _chatClient = chatClient;
        _logger = logger;
        _languageInstruction = PromptLanguage.Instruction(language, "the summaries");
    }

    private long _inputTokens;
    private long _outputTokens;
    private int _calls;
    private int _failedCalls;

    public virtual async Task<SummaryCompilation> CompileAsync(
        string? title,
        IReadOnlyList<CompileChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        _inputTokens = _outputTokens = 0;
        _calls = _failedCalls = 0;

        var ordered = chunks.OrderBy(c => c.Ordinal).ToList();
        var outline = DocumentOutline.Build(ordered.Select(c => new OutlineChunk(c.Ordinal, c.HeadingPath)));
        var notes = new List<string>();
        var sections = KeepSections(outline.Nodes, notes);
        var document = Plan(title, ordered, sections, out var sectionTasks);

        var tasks = new List<SummaryTask>();
        Collect(document, tasks);
        if (tasks.Count + 1 > MaxTasks)
        {
            notes.Add($"The document would need {tasks.Count + 1} summaries, more than the {MaxTasks} one compilation writes; it is not summarized.");
            return Result(new DocumentSummaries(), notes);
        }

        foreach (var level in tasks.GroupBy(t => t.Level).OrderBy(g => g.Key))
        {
            foreach (var batch in Batch(level.ToList()))
            {
                await SummarizeSectionsAsync(batch, cancellationToken);
            }
        }

        await SummarizeDocumentAsync(document, cancellationToken);

        var summaries = new DocumentSummaries { Document = document.Summary };
        foreach (var (key, task) in sectionTasks)
        {
            if (task.Summary is { } summary)
            {
                summaries.Sections[key] = summary;
            }
        }

        var failed = tasks.Count(t => t.Failed);
        if (failed > 0)
        {
            notes.Add($"{failed} of {tasks.Count} sections and parts could not be summarized.");
        }

        if (document.Failed)
        {
            notes.Add("The document summary could not be written.");
        }

        return Result(summaries, notes);
    }

    private SummaryCompilation Result(DocumentSummaries summaries, List<string> notes) => new()
    {
        Summaries = summaries,
        InputTokenCount = _inputTokens,
        OutputTokenCount = _outputTokens,
        CallCount = _calls,
        FailedCallCount = _failedCalls,
        Note = notes.Count == 0 ? null : string.Join(" ", notes)
    };

    /// <summary>The sections summarized on their own: all of them, or the levels from the top that fit in <see cref="MaxSections"/>.</summary>
    protected virtual IReadOnlyList<OutlineNode> KeepSections(IReadOnlyList<OutlineNode> nodes, List<string> notes)
    {
        if (nodes.Count <= MaxSections)
        {
            return nodes;
        }

        var depth = 0;
        while (nodes.Count(n => n.Depth <= depth + 1) <= MaxSections)
        {
            depth++;
        }

        notes.Add(depth == 0
            ? $"The document has {nodes.Count} sections, and more than {MaxSections} at its top level: no section is summarized on its own."
            : $"The document has {nodes.Count} sections, more than the {MaxSections} summarized: those deeper than level {depth} are summarized only within their parents.");
        return nodes.Where(n => n.Depth <= depth).ToList();
    }

    /// <summary>
    /// The summaries to write, as a tree under the document: each kept section owns the chunks it contains that no
    /// kept subsection contains, and its own text becomes parts when it is too long for one item.
    /// </summary>
    protected virtual SummaryTask Plan(
        string? title,
        IReadOnlyList<CompileChunk> chunks,
        IReadOnlyList<OutlineNode> sections,
        out List<(string Key, SummaryTask Task)> sectionTasks)
    {
        var document = new SummaryTask { Heading = title };
        var byKey = new Dictionary<string, SummaryTask>(StringComparer.Ordinal);
        sectionTasks = [];
        foreach (var node in sections)
        {
            var key = DocumentSummaries.Key(node.Path);
            var task = new SummaryTask
            {
                Heading = string.Join(" > ", DocumentOutline.ParseHeadingPath(node.HeadingPath).Select(h => h.Title)),
                Title = node.Title
            };
            byKey[key] = task;
            sectionTasks.Add((key, task));
        }

        var own = new Dictionary<SummaryTask, List<CompileChunk>>();
        foreach (var chunk in chunks)
        {
            var owner = sections
                .Where(n => n.FirstOrdinal <= chunk.Ordinal && chunk.Ordinal <= n.LastOrdinal)
                .MaxBy(n => n.Depth);
            var task = owner is null ? document : byKey[DocumentSummaries.Key(owner.Path)];
            if (!own.TryGetValue(task, out var list))
            {
                own[task] = list = [];
            }

            list.Add(chunk);
        }

        foreach (var (task, list) in own)
        {
            var parts = Split(list);
            if (parts.Count == 1)
            {
                (task.Text, task.TextCut) = parts[0];
                continue;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                task.Inputs.Add(new SummaryTask { Title = $"Part {i + 1}", Text = parts[i].Text, TextCut = parts[i].Cut });
            }
        }

        foreach (var node in sections)
        {
            var parent = node.Depth == 1 ? document : byKey[DocumentSummaries.Key(node.Path.Take(node.Depth - 1).ToList())];
            parent.Inputs.Add(byKey[DocumentSummaries.Key(node.Path)]);
        }

        SetLevel(document);
        return document;
    }

    /// <summary>Chunk texts joined into parts of at most <see cref="MaxTextLength"/>; a longer chunk is a part of its own, cut.</summary>
    private static List<(string Text, bool Cut)> Split(IReadOnlyList<CompileChunk> chunks)
    {
        var parts = new List<(string Text, bool Cut)>();
        var current = new StringBuilder();
        foreach (var chunk in chunks)
        {
            if (chunk.Text.Length > MaxTextLength)
            {
                Flush();
                parts.Add((TextTruncator.AtCharBoundary(chunk.Text, MaxTextLength), true));
                continue;
            }

            if (current.Length > 0 && current.Length + 2 + chunk.Text.Length > MaxTextLength)
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(chunk.Text);
        }

        Flush();
        return parts;

        void Flush()
        {
            if (current.Length > 0)
            {
                parts.Add((current.ToString(), false));
                current.Clear();
            }
        }
    }

    private static int SetLevel(SummaryTask task)
    {
        task.Level = task.Inputs.Count == 0 ? 0 : 1 + task.Inputs.Max(SetLevel);
        return task.Level;
    }

    private static void Collect(SummaryTask task, List<SummaryTask> into)
    {
        foreach (var input in task.Inputs)
        {
            Collect(input, into);
            into.Add(input);
        }
    }

    /// <summary>Items of one level, at most <see cref="ItemsPerCall"/> and <see cref="MaxCallTextLength"/> per call.</summary>
    protected virtual IEnumerable<List<(SummaryTask Task, string Rendered)>> Batch(List<SummaryTask> tasks)
    {
        var batch = new List<(SummaryTask, string)>();
        var length = 0;
        foreach (var task in tasks)
        {
            var rendered = Render(task, "Heading");
            if (batch.Count > 0 && (batch.Count == ItemsPerCall || length + rendered.Length > MaxCallTextLength))
            {
                yield return batch;
                batch = [];
                length = 0;
            }

            batch.Add((task, rendered));
            length += rendered.Length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>One item: its heading, its own text and its inputs' summaries, each wrapped as document data.</summary>
    protected virtual string Render(SummaryTask task, string headingLabel)
    {
        var item = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(task.Heading))
        {
            item.Append(headingLabel).Append(":\n").Append(PromptBoundary.WrapField(task.Heading)).Append('\n');
        }

        if (task.Text.Length > 0)
        {
            item.Append("Text:\n").Append(PromptBoundary.WrapDocument(task.Text)).Append('\n');
            if (task.TextCut)
            {
                item.Append(CutNote).Append('\n');
            }
        }

        if (task.Inputs.Count > 0)
        {
            var lines = new StringBuilder();
            var shown = 0;
            foreach (var input in task.Inputs)
            {
                var line = input.Title + ": " + input.Summary;
                if (shown > 0 && lines.Length + line.Length + 1 > MaxTextLength)
                {
                    break;
                }

                lines.Append(TextTruncator.AtCharBoundary(line, MaxTextLength)).Append('\n');
                shown++;
            }

            item.Append("Summaries of its subsections and parts:\n")
                .Append(PromptBoundary.WrapDocument(lines.ToString().TrimEnd()))
                .Append('\n');
            if (shown < task.Inputs.Count)
            {
                item.Append(MoreNote).Append('\n');
            }
        }

        return item.ToString();
    }

    protected virtual async Task SummarizeSectionsAsync(List<(SummaryTask Task, string Rendered)> batch, CancellationToken cancellationToken)
    {
        var user = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
        {
            user.Append("Item ").Append(i).Append(":\n").Append(batch[i].Rendered).Append('\n');
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, SectionInstructions + _languageInstruction + ContentInstruction + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, user.ToString())
        ];

        var answer = await AskAsync<SectionAnswer>(messages, "section_summaries", 200 + batch.Count * 200, cancellationToken);
        var answered = new HashSet<int>();
        foreach (var item in answer?.Summaries ?? [])
        {
            if (item.Index >= 0 && item.Index < batch.Count && answered.Add(item.Index))
            {
                batch[item.Index].Task.Summary = Clean(item.Summary, MaxSectionSummaryLength);
            }
        }

        for (var i = 0; i < batch.Count; i++)
        {
            batch[i].Task.Failed = !answered.Contains(i);
        }
    }

    protected virtual async Task SummarizeDocumentAsync(SummaryTask document, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, DocumentInstructions + _languageInstruction + ContentInstruction + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, Render(document, "Title"))
        ];

        var answer = await AskAsync<DocumentAnswer>(messages, "document_summary", 800, cancellationToken);
        document.Summary = Clean(answer?.Summary, MaxDocumentSummaryLength);
        document.Failed = answer is null;
    }

    /// <summary>
    /// One model call with a JSON schema answer, asked again once when the answer cannot be read; <c>null</c> when the
    /// call fails or no answer can be read.
    /// </summary>
    protected virtual async Task<T?> AskAsync<T>(List<ChatMessage> messages, string schemaName, int maxOutputTokens, CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            _calls++;
            try
            {
                var response = await _chatClient.GetResponseAsync(
                    messages,
                    new ChatOptions
                    {
                        ResponseFormat = ChatResponseFormat.ForJsonSchema(AIJsonUtilities.CreateJsonSchema(typeof(T)), schemaName),
                        MaxOutputTokens = maxOutputTokens
                    },
                    cancellationToken);
                _inputTokens += response.Usage?.InputTokenCount ?? 0;
                _outputTokens += response.Usage?.OutputTokenCount ?? 0;
                return LlmJson.Read<T>(response.Text) ?? throw new FormatException("The model returned no answer.");
            }
            catch (Exception exception) when (attempt < MaxAttempts && exception is System.Text.Json.JsonException or FormatException)
            {
                _logger.LogInformation("A summary answer ({Schema}) could not be read; asking again.", schemaName);
                _failedCalls++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "A summary call ({Schema}) failed; its items are left without summaries.", schemaName);
                _failedCalls++;
                return null;
            }
        }
    }

    /// <summary>One line, trimmed and cut; <c>null</c> for an empty answer.</summary>
    private static string? Clean(string? summary, int maxLength)
    {
        var line = Whitespace.Replace(summary ?? string.Empty, " ").Trim();
        return line.Length == 0 ? null : TextTruncator.AtCharBoundary(line, maxLength);
    }

    /// <summary>One summary to write: a section, a part of a long text, or the document.</summary>
    protected sealed class SummaryTask
    {
        /// <summary>A section's heading path, or the document's title.</summary>
        public string? Heading { get; init; }

        /// <summary>How its parent lists it: the section's own heading, or "Part n".</summary>
        public string? Title { get; init; }

        public string Text { get; set; } = string.Empty;

        public bool TextCut { get; set; }

        /// <summary>Its parts and subsections, in document order, summarized before it.</summary>
        public List<SummaryTask> Inputs { get; } = [];

        /// <summary>0 for an item with no inputs; one more than its deepest input otherwise.</summary>
        public int Level { get; set; }

        public string? Summary { get; set; }

        public bool Failed { get; set; }
    }

    protected sealed class SectionAnswer
    {
        public List<SectionSummary> Summaries { get; set; } = [];
    }

    protected sealed class SectionSummary
    {
        public int Index { get; set; }

        public string? Summary { get; set; }
    }

    protected sealed class DocumentAnswer
    {
        public string? Summary { get; set; }
    }
}
