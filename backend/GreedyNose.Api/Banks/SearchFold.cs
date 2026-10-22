using System.Globalization;
using System.Text;

namespace GreedyNose.Api.Banks;

/// <summary>
/// Folds text for the bank search so "societe" finds "Société Générale" and "uber" finds "Über Bank": canonical
/// decomposition with the combining marks dropped, plus the few letters that do not decompose. The hand-written
/// letters (ß, ø, æ, ...) work on any host; the accent folding relies on <c>string.Normalize</c> working on the
/// host (ICU on Linux). Text that cannot be normalized (a lone surrogate) is returned unchanged, so one odd name
/// never fails a search. Case is left to the caller's ordinal-ignore-case comparison.
/// </summary>
public static class SearchFold
{
    public static string Fold(string text)
    {
        string decomposed;
        try
        {
            decomposed = text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in decomposed)
        {
            switch (c)
            {
                case 'ß': builder.Append("ss"); break;
                case 'ẞ': builder.Append("SS"); break;
                case 'ø': builder.Append('o'); break;
                case 'Ø': builder.Append('O'); break;
                case 'æ': builder.Append("ae"); break;
                case 'Æ': builder.Append("AE"); break;
                case 'œ': builder.Append("oe"); break;
                case 'Œ': builder.Append("OE"); break;
                case 'ł': builder.Append('l'); break;
                case 'Ł': builder.Append('L'); break;
                case 'đ': builder.Append('d'); break;
                case 'Đ': builder.Append('D'); break;
                default:
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
