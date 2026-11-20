using System.Globalization;
using System.Text;

namespace Cafe.Server.Receipts;

public enum EscPosAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// Our own ESC/POS byte encoder (no third-party package). Text is encoded in code page 437; characters outside it
/// lose their diacritics first (Vietnamese prints as plain letters), and whatever is still unmappable prints as '?', and control characters never reach the printer as commands (only <see cref="LineFeed"/> emits 0x0A).
/// </summary>
public sealed class EscPosEncoder
{
    private static readonly Encoding Cp437 = CreateCp437();

    private readonly List<byte> _bytes = [];

    private static Encoding CreateCp437() =>
        System.Text.CodePagesEncodingProvider.Instance.GetEncoding(
            437, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback)
        ?? throw new InvalidOperationException("Code page 437 is not available.");

    /// <summary>ESC @ : reset the printer.</summary>
    public EscPosEncoder Initialize() => Raw(0x1B, 0x40);

    /// <summary>ESC a n.</summary>
    public EscPosEncoder Align(EscPosAlign align) => Raw(0x1B, 0x61, (byte)align);

    /// <summary>ESC E n.</summary>
    public EscPosEncoder Bold(bool on) => Raw(0x1B, 0x45, on ? (byte)1 : (byte)0);

    /// <summary>GS ! n with the magnification byte the plan specifies (01 on, 00 off).</summary>
    public EscPosEncoder DoubleHeight(bool on) => Raw(0x1D, 0x21, on ? (byte)1 : (byte)0);

    /// <summary>GS V B 0 : partial cut after feeding.</summary>
    public EscPosEncoder Cut() => Raw(0x1D, 0x56, 0x42, 0x00);

    public EscPosEncoder LineFeed(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _bytes.Add(0x0A);
        }

        return this;
    }

    /// <summary>Appends text. A newline inside the text becomes a <see cref="LineFeed"/>; nothing else is a command.</summary>
    public EscPosEncoder Text(string text)
    {
        var parts = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                _bytes.Add(0x0A);
            }

            _bytes.AddRange(Cp437.GetBytes(Normalise(parts[i])));
        }

        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>The text exactly as it will print (same replacements), so layout can measure columns.</summary>
    public static string ToPrintable(string? text) =>
        Cp437.GetString(Cp437.GetBytes(Normalise((text ?? string.Empty).Replace("\r", " ").Replace("\n", " "))));

    private EscPosEncoder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    private static string Normalise(string text)
    {
        // Compose first so a decomposed "e" + acute is one character that CP437 can print as-is.
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (rune.Value > 0xFFFF)
            {
                // A surrogate pair (emoji) is one character on paper: exactly one '?'.
                sb.Append('?');
            }
            else if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator)
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(Fold(rune.ToString()));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keeps a character CP437 can print (é, ñ, ü ...). Otherwise folds diacritics away so Vietnamese stays readable
    /// ("ạ" prints as "a", "đ" as "d"); anything still unmappable stays as is and prints as '?'.
    /// </summary>
    private static string Fold(string character)
    {
        if (CanEncode(character))
        {
            return character;
        }

        var folded = new StringBuilder(character.Length);
        foreach (var c in character.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(c);
            }
        }

        var result = folded.ToString();
        return result.Length > 0 && CanEncode(result) ? result : character;
    }

    private static bool CanEncode(string text) => Cp437.GetString(Cp437.GetBytes(text)) == text;
}
