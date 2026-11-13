using System;
using System.Collections.Generic;

namespace Dignite.Vault.Index.Ai;

/// <summary>
/// The language an LLM is told to write in, named from a fixed table by the document's language tag (Extract's), so
/// the instructions that carry it stay compile-time constants. Without a known tag the model is asked for the text's
/// own language, which a model may misread: kanji-heavy Japanese has come back as Chinese.
/// </summary>
public static class PromptLanguage
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "Japanese",
        ["zh"] = "Chinese",
        ["zh-Hans"] = "Simplified Chinese",
        ["zh-CN"] = "Simplified Chinese",
        ["zh-Hant"] = "Traditional Chinese",
        ["zh-TW"] = "Traditional Chinese",
        ["en"] = "English",
        ["ko"] = "Korean"
    };

    /// <summary>The English name of the language of <paramref name="tag"/>, or of its primary subtag; <c>null</c> when unknown.</summary>
    public static string? Name(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        if (Names.TryGetValue(trimmed, out var name))
        {
            return name;
        }

        var dash = trimmed.IndexOf('-');
        return dash > 0 && Names.TryGetValue(trimmed[..dash], out name) ? name : null;
    }

    /// <summary>
    /// The instruction that names the language, a constant from the table: "Write … in Japanese, the language of the
    /// document, whatever script a passage seems to use."; or, without a known tag, "in the language of the passage".
    /// </summary>
    public static string Instruction(string? tag, string what)
        => Name(tag) is { } name
            ? " Write " + what + " in " + name + ", the language of the document, whatever script a passage seems to use."
            : " Write " + what + " in the language of the passage.";
}
