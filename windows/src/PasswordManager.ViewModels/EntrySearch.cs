using System.Globalization;
using System.Text;

namespace PasswordManager.ViewModels;

/// <summary>
/// The matching rules of the entry list search: text is folded (accents and case removed), the query is split into
/// terms, and an entry matches when every term is found in at least one of its folded fields.
/// </summary>
public static class EntrySearch
{
    /// <summary>
    /// Removes accents and case: normalizes to FormD, drops the non-spacing marks and upper-cases (invariant). Text
    /// that is not valid UTF-16 (a lone surrogate) cannot be normalized, so it is only upper-cased; this never throws.
    /// </summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        string decomposed;
        try
        {
            decomposed = text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return text.ToUpperInvariant();
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToUpperInvariant();
    }

    /// <summary>Splits <paramref name="query"/> on whitespace and folds each term; terms that fold to nothing are dropped.</summary>
    public static string[] Terms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var terms = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var term = Fold(part);
            if (term.Length > 0)
            {
                terms.Add(term);
            }
        }

        return [.. terms];
    }

    /// <summary>
    /// True when every term is an ordinal substring of at least one folded field. A term never spans two fields, and
    /// an empty list of terms matches everything.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> foldedFields, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(foldedFields);
        ArgumentNullException.ThrowIfNull(terms);

        for (var t = 0; t < terms.Count; t++)
        {
            var term = terms[t];
            var found = false;
            for (var f = 0; f < foldedFields.Count; f++)
            {
                if (foldedFields[f].Contains(term, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}
