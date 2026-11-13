using System;
using System.Text.Json;

namespace Dignite.Vault.Index.Ai;

/// <summary>Reads a model's JSON answer, also when the model put it in a Markdown code fence despite the schema.</summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <exception cref="JsonException">The answer is not the JSON asked for.</exception>
    public static T? Read<T>(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            json = start >= 0 && end > start ? json[start..(end + 1)] : json;
        }

        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
