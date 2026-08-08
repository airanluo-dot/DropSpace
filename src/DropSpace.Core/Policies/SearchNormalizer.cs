using System.Globalization;
using System.Text;

namespace DropSpace.Core.Policies;

public static class SearchNormalizer
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var decomposed = value.Normalize(NormalizationForm.FormKD);
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
