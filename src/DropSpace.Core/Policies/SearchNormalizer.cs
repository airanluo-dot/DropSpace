using System.Globalization;
using System.Text;

namespace DropSpace.Core.Policies;

public static class SearchNormalizer
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            // Windows clipboard strings can contain unpaired UTF-16 surrogates. Match
            // UTF-8 persistence's replacement behavior instead of rejecting the item or query.
            var repaired = string.Concat(value.EnumerateRunes().Select(rune => rune.ToString()));
            decomposed = repaired.Normalize(NormalizationForm.FormKD);
        }
        var builder = new StringBuilder(decomposed.Length);
        var previousWhitespace = false;

        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                }

                previousWhitespace = true;
                continue;
            }

            previousWhitespace = false;
            builder.Append(rune.ToString().ToLowerInvariant());
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
