using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>RTF text/character/paragraph interchange. Tables are flattened to paragraphs.</summary>
public sealed class RtfDocumentFormat : TextDocumentFormat
{
    private sealed record ParserState(TextStyle Text, ParagraphStyle Paragraph, bool Skip = false, int UnicodeFallback = 1);
    public override string Name => "Rich Text Format";
    public override IReadOnlyList<string> Extensions => [".rtf"];

    public override async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        // RTF is an ANSI byte stream with explicit Unicode escape sequences.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return await Task.Run(() => Parse(Encoding.GetEncoding(1252).GetString(bytes)), cancellationToken);
    }

    public override FlowDocument Parse(string text)
    {
        if (!text.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
            throw new FormatException("Not an RTF document.");
        if (text.Length > 32 * 1024 * 1024) throw new FormatException("RTF is too large.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(1252);
        var fonts = new Dictionary<int, string>();
        var fontTable = ExtractGroup(text, "\\fonttbl");
        foreach (Match match in Regex.Matches(fontTable, @"\\f(\d+)[^;{}]*?\s([^;{}\\]+);"))
            fonts[int.Parse(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
        var colors = new List<string?> { null };
        var colorTable = ExtractGroup(text, "\\colortbl");
        foreach (Match match in Regex.Matches(colorTable, @"\\red(\d+)\\green(\d+)\\blue(\d+)\s*;"))
            colors.Add($"#{Math.Clamp(int.Parse(match.Groups[1].Value), 0, 255):X2}{Math.Clamp(int.Parse(match.Groups[2].Value), 0, 255):X2}{Math.Clamp(int.Parse(match.Groups[3].Value), 0, 255):X2}");

        var paragraphs = new List<Block>();
        var runs = new List<RichRun>();
        var buffer = new StringBuilder();
        var state = new ParserState(TextStyle.Default, ParagraphStyle.Default);
        var bufferStyle = state.Text;
        var states = new Stack<ParserState>();
        var fallback = 0;
        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new(buffer.ToString(), bufferStyle)); buffer.Clear();
        }
        void Append(char ch)
        {
            if (state.Skip) return;
            if (fallback > 0) { fallback--; return; }
            if (bufferStyle != state.Text) { Flush(); bufferStyle = state.Text; }
            buffer.Append(ch);
        }
        void Paragraph()
        {
            Flush();
            paragraphs.Add(new Paragraph(runs) { Style = state.Paragraph, DefaultStyle = state.Text });
            runs.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '{')
            {
                if (states.Count > 128) throw new FormatException("RTF nesting is too deep.");
                states.Push(state); continue;
            }
            if (ch == '}')
            {
                if (states.Count == 0) throw new FormatException("Unbalanced RTF group.");
                Flush(); state = states.Pop(); bufferStyle = state.Text; continue;
            }
            if (ch is '\r' or '\n') continue;
            if (ch != '\\') { Append(ch); continue; }
            if (++i >= text.Length) break;
            ch = text[i];
            if (ch is '\\' or '{' or '}') { Append(ch); continue; }
            if (ch == '*') { state = state with { Skip = true }; continue; }
            if (ch == '~') { Append('\u00a0'); continue; }
            if (ch == '_') { Append('\u2011'); continue; }
            if (ch == '-'){ Append('\u00ad'); continue; }
            if (ch == '\'' && i + 2 < text.Length)
            {
                if (byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    Append(ansi.GetString([value])[0]);
                i += 2; continue;
            }
            if (!char.IsAsciiLetter(ch)) continue;
            var start = i;
            while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
            var word = text[start..i];
            var numberStart = i;
            if (i < text.Length && text[i] == '-') i++;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            var hasNumber = int.TryParse(text.AsSpan(numberStart, i - numberStart), out var number);
            if (i < text.Length && text[i] == ' ') i++;
            i--;
            if (word == "bin")
            {
                if (!hasNumber || number < 0 || number > text.Length - i - 1) throw new FormatException("Invalid binary RTF payload.");
                i += number; continue;
            }
            if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or
                "listtable" or "listoverridetable" or "generator" or "header" or "footer" or "footnote" or
                "annotation" or "fldinst" or "pntext" or "listtext")
            { state = state with { Skip = true }; continue; }
            if (state.Skip) continue;
            Flush(); bufferStyle = state.Text;
            var enabled = !hasNumber || number != 0;
            switch (word)
            {
                case "ansicpg":
                    try { ansi = Encoding.GetEncoding(number); } catch (ArgumentException) { }
                    break;
                case "par": Paragraph(); break;
                case "line": Append('\u2028'); break;
                case "tab": Append('\t'); break;
                case "emdash": Append('\u2014'); break;
                case "endash": Append('\u2013'); break;
                case "lquote": Append('\u2018'); break;
                case "rquote": Append('\u2019'); break;
                case "ldblquote": Append('\u201c'); break;
                case "rdblquote": Append('\u201d'); break;
                case "bullet": Append('\u2022'); break;
                case "u":
                    if (number is < short.MinValue or > ushort.MaxValue) throw new FormatException("Invalid Unicode escape.");
                    fallback = 0; Append(unchecked((char)number)); fallback = state.UnicodeFallback; break;
                case "uc": state = state with { UnicodeFallback = Math.Clamp(number, 0, 16) }; break;
                case "b": state = state with { Text = state.Text with { Bold = enabled } }; break;
                case "i": state = state with { Text = state.Text with { Italic = enabled } }; break;
                case "ul": state = state with { Text = state.Text with { Underline = enabled } }; break;
                case "ulnone": state = state with { Text = state.Text with { Underline = false } }; break;
                case "strike": state = state with { Text = state.Text with { Strikethrough = enabled } }; break;
                case "sub": state = state with { Text = state.Text with { Baseline = Baseline.Subscript } }; break;
                case "super": state = state with { Text = state.Text with { Baseline = Baseline.Superscript } }; break;
                case "nosupersub": state = state with { Text = state.Text with { Baseline = Baseline.Normal } }; break;
                case "fs": state = state with { Text = state.Text with { FontSize = Math.Clamp(number * 2d / 3, 1, 512) } }; break;
                case "f": state = state with { Text = state.Text with { FontFamily = fonts.GetValueOrDefault(number) } }; break;
                case "cf": state = state with { Text = state.Text with { Foreground = number >= 0 && number < colors.Count ? colors[number] : null } }; break;
                case "highlight": state = state with { Text = state.Text with { Background = number >= 0 && number < colors.Count ? colors[number] : null } }; break;
                case "plain": state = state with { Text = TextStyle.Default }; break;
                case "pard": state = state with { Paragraph = ParagraphStyle.Default }; break;
                case "ql": state = state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Left } }; break;
                case "qc": state = state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Center } }; break;
                case "qr": state = state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Right } }; break;
                case "qj": state = state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Justify } }; break;
                case "rtlpar": state = state with { Paragraph = state.Paragraph with { RightToLeft = true } }; break;
                case "ltrpar": state = state with { Paragraph = state.Paragraph with { RightToLeft = false } }; break;
                case "li": state = state with { Paragraph = state.Paragraph with { Indent = Math.Clamp(number / 15d, 0, 1000) } }; break;
                case "sb": state = state with { Paragraph = state.Paragraph with { SpaceBefore = Math.Clamp(number / 15d, 0, 1000) } }; break;
                case "sa": state = state with { Paragraph = state.Paragraph with { SpaceAfter = Math.Clamp(number / 15d, 0, 1000) } }; break;
            }
            bufferStyle = state.Text;
        }
        if (states.Count != 0) throw new FormatException("Unbalanced RTF group.");
        Flush();
        if (runs.Count > 0 || paragraphs.Count == 0) Paragraph();
        var document = new FlowDocument(paragraphs); document.Validate(); return document;
    }

    public override string Serialize(FlowDocument document)
    {
        var paragraphs = new DocumentIndex(document).Paragraphs.Select(p => p.Paragraph).ToArray();
        var styles = paragraphs.SelectMany(p => p.Runs.Select(r => r.Style).Append(p.DefaultStyle)).ToArray();
        var fonts = styles.Select(s => s.FontFamily ?? "Arial").Distinct().ToArray();
        var colors = styles.SelectMany(s => new[] { s.Foreground, s.Background }).Where(c => c is not null).Distinct().ToArray();
        var b = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl");
        for (var i = 0; i < fonts.Length; i++) b.Append("{\\f").Append(i).Append(' ').Append(Escape(fonts[i].Replace(";", ""))).Append(";}");
        b.Append("}{\\colortbl ;");
        foreach (var color in colors)
        {
            var rgb = color![^6..];
            b.Append("\\red").Append(Convert.ToInt32(rgb[..2], 16)).Append("\\green").Append(Convert.ToInt32(rgb[2..4], 16))
                .Append("\\blue").Append(Convert.ToInt32(rgb[4..], 16)).Append(';');
        }
        b.Append('}');
        foreach (var p in paragraphs)
        {
            b.Append("\\pard").Append(p.Style.Alignment switch { ParagraphAlignment.Center => "\\qc", ParagraphAlignment.Right => "\\qr", ParagraphAlignment.Justify => "\\qj", _ => "\\ql" });
            b.Append(p.Style.RightToLeft ? "\\rtlpar" : "\\ltrpar").Append("\\li").Append((int)(p.Style.Indent * 15))
                .Append("\\sb").Append((int)(p.Style.SpaceBefore * 15)).Append("\\sa").Append((int)(p.Style.SpaceAfter * 15)).Append(' ');
            foreach (var run in p.Runs)
            {
                var s = run.Style;
                b.Append("{\\plain\\f").Append(Array.IndexOf(fonts, s.FontFamily ?? "Arial")).Append("\\fs").Append((int)Math.Round(s.FontSize * 1.5))
                    .Append(s.Bold ? "\\b" : "").Append(s.Italic ? "\\i" : "").Append(s.Underline ? "\\ul" : "").Append(s.Strikethrough ? "\\strike" : "")
                    .Append(s.Baseline switch { Baseline.Subscript => "\\sub", Baseline.Superscript => "\\super", _ => "" })
                    .Append("\\cf").Append(s.Foreground is null ? 0 : Array.IndexOf(colors, s.Foreground) + 1)
                    .Append("\\highlight").Append(s.Background is null ? 0 : Array.IndexOf(colors, s.Background) + 1)
                    .Append(' ').Append(Escape(run.PlainText)).Append('}');
            }
            b.Append("\\par\n");
        }
        return b.Append('}').ToString();
    }

    private static string Escape(string text)
    {
        var b = new StringBuilder();
        foreach (var ch in text)
            if (ch is '\\' or '{' or '}') b.Append('\\').Append(ch);
            else if (ch == '\t') b.Append("\\tab ");
            else if (ch == '\u2028') b.Append("\\line ");
            else if (ch > 127) b.Append("\\u").Append(unchecked((short)ch)).Append('?');
            else b.Append(ch);
        return b.ToString();
    }
    private static string ExtractGroup(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '{' or '}' or '\\') { i++; continue; }
            if (text[i] == '{') depth++;
            if (text[i] == '}' && --depth == 0) return text[start..i];
        }
        return "";
    }
}
