using System.Text;

namespace CursorTranslator.Services;

public static class TranslationUnitCounter
{
    public static long Count(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        long count = 0;
        var inWord = false;

        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            if (IsHanIdeograph(rune))
            {
                count++;
                inWord = false;
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (!inWord) count++;
                inWord = true;
            }
            else if (IsCombiningMark(rune) && inWord)
            {
                // Combining marks are part of the letter immediately before them.
            }
            else if (IsWordJoiner(rune)
                && inWord
                && index + 1 < runes.Length
                && Rune.IsLetterOrDigit(runes[index + 1])
                && !IsHanIdeograph(runes[index + 1]))
            {
                // Keep contractions and hyphenated English words together.
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    private static bool IsWordJoiner(Rune rune)
        => rune.Value is '\'' or 0x2019 or '-' or 0x2010 or 0x2011;

    private static bool IsCombiningMark(Rune rune)
        => Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark;

    private static bool IsHanIdeograph(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x323AF;
    }
}
