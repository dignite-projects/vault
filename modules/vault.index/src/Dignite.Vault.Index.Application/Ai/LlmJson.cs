using System;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dignite.Vault.Index.Ai;

/// <summary>
/// Reads a model's JSON answer, also when the model put it in a Markdown code fence despite the schema, which
/// Microsoft.Extensions.AI's <see cref="ChatResponse{T}"/> does not read.
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The answer of a <c>GetResponseAsync&lt;T&gt;</c> call: its own result, or the fenced JSON its text holds.
    /// </summary>
    /// <exception cref="JsonException">The answer is not the JSON asked for.</exception>
    /// <exception cref="FormatException">The model returned no answer.</exception>
    public static T Read<T>(ChatResponse<T> response)
        => response.TryGetResult(out var result)
            ? result
            : Read<T>(response.Text) ?? throw new FormatException("The model returned no answer.");

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
