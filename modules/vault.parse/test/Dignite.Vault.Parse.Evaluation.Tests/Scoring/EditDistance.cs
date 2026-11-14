using System;

namespace Dignite.Vault.Parse.Evaluation.Scoring;

public static class EditDistance
{
    /// <summary>Levenshtein distance (insert, delete, substitute; each costs 1).</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ai = a[i - 1];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = ai == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary><c>1 - distance / max(length)</c>, in [0, 1]; two empty strings are identical.</summary>
    public static double Similarity(string a, string b)
    {
        var length = Math.Max(a.Length, b.Length);
        return length == 0 ? 1d : 1d - (double)Levenshtein(a, b) / length;
    }

    /// <summary>
    /// Where <paramref name="pattern"/> occurs in <paramref name="text"/> with the fewest edits (semi-global alignment:
    /// the occurrence may start and end anywhere in the text). Returns that distance and the end index of the occurrence.
    /// </summary>
    public static (int Distance, int End) BestOccurrence(string pattern, string text)
    {
        if (pattern.Length == 0)
        {
            return (0, 0);
        }

        if (text.Length == 0)
        {
            return (pattern.Length, 0);
        }

        // Rows over the pattern, columns over the text; row 0 is free so the occurrence can start at any column.
        var previous = new int[text.Length + 1];
        var current = new int[text.Length + 1];
        for (var i = 1; i <= pattern.Length; i++)
        {
            current[0] = i;
            var pi = pattern[i - 1];
            for (var j = 1; j <= text.Length; j++)
            {
                var cost = pi == text[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        var best = int.MaxValue;
        var end = 0;
        for (var j = 0; j <= text.Length; j++)
        {
            if (previous[j] < best)
            {
                best = previous[j];
                end = j;
            }
        }

        return (best, end);
    }
}
