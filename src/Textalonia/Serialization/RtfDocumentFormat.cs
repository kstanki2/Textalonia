using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Bounded RTF interchange for flow text, lists, tables, sections, links and embedded PNG/JPEG images.</summary>
public sealed class RtfDocumentFormat : TextDocumentFormat
{
    public override string Name => "Rich Text Format";
    public override IReadOnlyList<string> Extensions => [".rtf"];
    public override async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Preserve every byte while identifying groups. Text literals are decoded only after
        // the document code page is known; binary picture bytes keep their original identity.
        return await Task.Run(() => new Reader(Tokenize(Encoding.GetEncoding(1252).GetString(bytes)), true).Read(), cancellationToken);
    }
    public override FlowDocument Parse(string text)
    {
        if (text.Length > 32 * 1024 * 1024) throw new FormatException("RTF is too large.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new Reader(Tokenize(text)).Read();
    }

    private abstract record Node(int Offset);
    private sealed record Control(string Word, int? Number, int At) : Node(At);
    private sealed record Literal(string Value, bool Ansi, int At) : Node(At);
    private sealed record Binary(string Value, int At) : Node(At);
    private sealed record Group(List<Node> Nodes, int At) : Node(At)
    {
        public string? Destination => Nodes.OfType<Control>().FirstOrDefault(c => c.Word != "*")?.Word;
        public int? Number(string name) => Nodes.OfType<Control>().LastOrDefault(c => c.Word == name)?.Number;
        public IEnumerable<Group> Groups(string name) => Nodes.OfType<Group>().Where(g => g.Destination == name);
    }

    // Validate all groups, including ignored destinations, before building a model. Binary payloads
    // are length-delimited and never scanned for braces or control words.
    private static Group Tokenize(string text)
    {
        var position = 0; var count = 0;
        Group ReadGroup(int depth)
        {
            if (depth > 128) throw new FormatException("RTF nesting is too deep.");
            var start = position++; var children = new List<Node>();
            while (position < text.Length)
            {
                if (++count > 1_000_000) throw new FormatException("RTF contains too many tokens.");
                var at = position; var ch = text[position++];
                if (ch == '}') return new(children, start);
                if (ch == '{') { position--; children.Add(ReadGroup(depth + 1)); continue; }
                if (ch is '\r' or '\n') continue;
                if (ch != '\\')
                {
                    while (position < text.Length && text[position] is not ('{' or '}' or '\\' or '\r' or '\n')) position++;
                    children.Add(new Literal(text[at..position], false, at)); continue;
                }
                if (position == text.Length) throw new FormatException("Truncated RTF escape.");
                ch = text[position++];
                if (ch is '\\' or '{' or '}') { children.Add(new Literal(ch.ToString(), false, at)); continue; }
                if (ch == '\'')
                {
                    if (position + 2 > text.Length || !byte.TryParse(text.AsSpan(position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                        throw new FormatException("Invalid RTF hexadecimal escape.");
                    children.Add(new Literal(((char)value).ToString(), true, at)); position += 2; continue;
                }
                if (!char.IsAsciiLetter(ch)) { children.Add(new Control(ch.ToString(), null, at)); continue; }
                var wordStart = position - 1;
                while (position < text.Length && char.IsAsciiLetter(text[position])) position++;
                var word = text[wordStart..position]; var numberStart = position;
                if (position < text.Length && text[position] == '-') position++;
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                int? number = null;
                if (position != numberStart)
                {
                    if (!int.TryParse(text.AsSpan(numberStart, position - numberStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        throw new FormatException("Invalid RTF numeric parameter.");
                    number = parsed;
                }
                if (position < text.Length && text[position] == ' ') position++;
                if (word == "bin")
                {
                    if (number is null or < 0 || number > text.Length - position) throw new FormatException("Invalid binary RTF payload.");
                    children.Add(new Binary(text.Substring(position, number.Value), at)); position += number.Value;
                }
                else children.Add(new Control(word, number, at));
            }
            throw new FormatException("Unbalanced RTF group.");
        }
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        if (position == text.Length || text[position] != '{') throw new FormatException("Not an RTF document.");
        var root = ReadGroup(0);
        if (root.Nodes.FirstOrDefault() is not Control { Word: "rtf", Number: 1 }) throw new FormatException("Not an RTF 1 document.");
        if (text.AsSpan(position).Trim().Length != 0) throw new FormatException("Data follows the RTF root group.");
        return root;
    }

    private sealed record State(TextStyle Text, ParagraphStyle Paragraph, int UnicodeFallback = 1, int List = 0);
    private sealed record ListInfo(Guid Id, ListDefinition Definition, ImmutableDictionary<int, int> Starts);
    private sealed record CellInfo(int Right = 0, string? Background = null, bool HorizontalStart = false,
        bool HorizontalContinue = false, bool VerticalStart = false, bool VerticalContinue = false);
    private sealed class Row
    {
        public List<CellInfo> Definitions { get; } = [];
        public List<TableCell> Cells { get; } = [];
        public List<Block> Blocks { get; } = [];
        public TableRowSizing Sizing { get; set; } = new();
    }
    private sealed class Reader(Group root, bool byteSource = false, bool readingStory = false)
    {
        private readonly Dictionary<int, string> _fonts = [];
        private readonly List<string?> _colors = [null];
        private readonly Dictionary<int, ListInfo> _lists = [];
        private readonly HashSet<(int List, int Level)> _startedLists = [];
        private readonly HashSet<int> _seenLists = [];
        private readonly HashSet<string> _reported = [];
        private readonly List<Block> _blocks = [];
        private readonly List<Block> _section = [];
        private readonly List<RichRun> _runs = [];
        private readonly List<Row> _rows = [];
        private readonly Stack<Row> _outerRows = [];
        private readonly ImmutableDictionary<string, DocumentResource>.Builder _resources = ImmutableDictionary.CreateBuilder<string, DocumentResource>();
        private readonly StringBuilder _buffer = new();
        private Encoding _ansi = Encoding.GetEncoding(1252);
        private TextStyle _bufferStyle = TextStyle.Default;
        private State _lastState = new(TextStyle.Default, ParagraphStyle.Default);
        private TextStyle _defaultText = TextStyle.Default;
        private Row? _row;
        private CellInfo _cell = new();
        private int _fallback;
        private bool _sectionActive;
        private bool _nestedProperties;
        private bool _flattenFields;
        private long _resourceBytes;
        private readonly string _resourcePrefix = Guid.NewGuid().ToString("N");
        private readonly ImmutableDictionary<Guid, DocumentStory>.Builder _stories = ImmutableDictionary.CreateBuilder<Guid, DocumentStory>();
        private readonly List<DocumentNote> _notes = [];
        private readonly List<DocumentSection> _physicalSections = [];
        private DocumentSection _physicalSection = new();
        private bool _hasPhysicalSections;
        private bool _oddEvenHeaders;
        private NoteSettings _footnoteSettings = new();
        private NoteSettings _endnoteSettings = new() { Placement = NotePlacement.DocumentEnd };

        private void Loss(string code, string feature, string fallback, int? offset = null)
        {
            if (_reported.Add(code + feature)) ConversionDiagnostics.Report(code, feature, fallback, sourceLocation: offset is null ? null : $"rtf:{offset}");
        }
        private string? Color(int number) => number >= 0 && number < _colors.Count ? _colors[number] : null;
        private double Range(double value, double minimum, double maximum, string feature, int? offset = null)
        {
            if (value < minimum || value > maximum) Loss("rtf.value-range", feature, "Clamped the value to the supported model range.", offset);
            return Math.Clamp(value, minimum, maximum);
        }
        private void Flush()
        {
            if (_buffer.Length == 0) return;
            _runs.Add(new(_buffer.ToString(), _bufferStyle)); _buffer.Clear();
        }
        private void Append(string text, State state)
        {
            if (_bufferStyle != state.Text) { Flush(); _bufferStyle = state.Text; }
            foreach (var ch in text) if (_fallback > 0) _fallback--; else _buffer.Append(ch);
            _lastState = state;
        }
        private void Paragraph(State state)
        {
            Flush(); var style = state.Paragraph;
            if (state.List != 0)
            {
                if (_lists.TryGetValue(state.List, out var list))
                {
                    var level = list.Definition.Level(style.ListLevel, ListKind.Numbered);
                    var first = _startedLists.Add((state.List, style.ListLevel));
                    var definition = list.Definition;
                    if (_seenLists.Add(state.List) && style.ListLevel > 0)
                    {
                        // A newly restarted nested instance may have no visible ancestor
                        // paragraph. Seed those counters without changing later restart defaults.
                        foreach (var ancestorLevel in list.Starts.Keys.Where(i => i < style.ListLevel)) _startedLists.Add((state.List, ancestorLevel));
                        definition = new() { Levels = Enumerable.Range(0, Math.Max(style.ListLevel + 1, definition.Levels.Length))
                            .Select(i => i < style.ListLevel && list.Starts.TryGetValue(i, out var ancestor) ? definition.Level(i, level.Kind) with { Start = ancestor } : definition.Level(i, level.Kind)).ToImmutableArray() };
                    }
                    style = style with { List = level.Kind, ListId = list.Id, ListDefinition = definition,
                        ListStart = first && list.Starts.TryGetValue(style.ListLevel, out var start) ? start : null };
                }
                else Loss("rtf.undefined-list", "Undefined list override", "Imported the paragraph without numbering.");
            }
            var paragraph = new Paragraph(_runs) { Style = style, DefaultStyle = state.Text };
            if (_row is not null) _row.Blocks.Add(paragraph);
            else { EndTable(); _section.Add(paragraph); }
            _runs.Clear(); _lastState = state;
        }
        private void PendingParagraph(State state) { if (_buffer.Length > 0 || _runs.Count > 0) Paragraph(state); }
        private void EndCell(State state)
        {
            if (_row is null) throw new FormatException("RTF cell outside a table row.");
            PendingParagraph(state);
            if (_row.Blocks.Count == 0) _row.Blocks.Add(new Paragraph { Style = state.Paragraph, DefaultStyle = state.Text });
            if (_row.Cells.Count >= 100 || _row.Cells.Count >= _row.Definitions.Count && (_outerRows.Count == 0 || _row.Definitions.Count != 0)) throw new FormatException("RTF cell has no cell boundary.");
            _row.Cells.Add(new() { Blocks = _row.Blocks.ToImmutableArray(), Background = _row.Cells.Count < _row.Definitions.Count ? _row.Definitions[_row.Cells.Count].Background : null });
            _row.Blocks.Clear();
        }
        private void NestedRow(State state, int offset)
        {
            if (_row is null || _outerRows.Count >= 32) throw new FormatException("Invalid nested RTF table depth.");
            PendingParagraph(state);
            _outerRows.Push(_row); _row = new(); _cell = new();
            Loss("rtf.nested-table", "Nested table", "Flattened nested table cells to paragraphs inside the parent cell.", offset);
        }
        private void EndRow()
        {
            if (_row is null || _row.Cells.Count == 0 || _row.Cells.Count != _row.Definitions.Count || _row.Blocks.Count > 0 || _runs.Count > 0 || _buffer.Length > 0)
                throw new FormatException("Incomplete RTF table row.");
            if (_outerRows.Count > 0)
            {
                var nested = _row; _row = _outerRows.Pop();
                foreach (var cell in nested.Cells) _row.Blocks.AddRange(cell.Blocks);
            }
            else
            {
                if (_rows.Count >= 1000) throw new FormatException("Too many RTF table rows.");
                _rows.Add(_row); _row = null;
            }
        }
        private void EndTable()
        {
            if (_rows.Count == 0) return;
            var columns = _rows.Max(r => r.Cells.Count);
            if (_rows.Any(r => r.Cells.Count != columns)) Loss("rtf.ragged-table", "Rows with different cell grids", "Padded short rows with empty cells.");
            var cells = _rows.Select(r => r.Cells.Concat(Enumerable.Range(r.Cells.Count, columns - r.Cells.Count).Select(_ => new TableCell())).ToArray()).ToArray();
            var owners = new (int Row, int Column)[cells.Length, columns];
            for (var r = 0; r < cells.Length; r++)
                for (var c = 0; c < columns; c++)
                {
                    owners[r, c] = (r, c);
                    if (c >= _rows[r].Definitions.Count) continue;
                    var definition = _rows[r].Definitions[c];
                    if (definition.HorizontalContinue)
                    {
                        if (c == 0 || !(_rows[r].Definitions[c - 1].HorizontalStart || _rows[r].Definitions[c - 1].HorizontalContinue)) throw new FormatException("Invalid horizontal RTF merge.");
                        var owner = owners[r, c - 1]; owners[r, c] = owner;
                        if (owner.Row == r) cells[r][owner.Column] = cells[r][owner.Column] with { ColumnSpan = c - owner.Column + 1 };
                    }
                    else if (definition.VerticalContinue)
                    {
                        if (r == 0 || c >= _rows[r - 1].Definitions.Count || !(_rows[r - 1].Definitions[c].VerticalStart || _rows[r - 1].Definitions[c].VerticalContinue)) throw new FormatException("Invalid vertical RTF merge.");
                        var owner = owners[r - 1, c]; owners[r, c] = owner;
                        cells[owner.Row][owner.Column] = cells[owner.Row][owner.Column] with { RowSpan = r - owner.Row + 1 };
                    }
                }
            var first = _rows[0].Definitions;
            var widths = first.Count == columns ? first.Select((cell, i) => (cell.Right - (i == 0 ? 0 : first[i - 1].Right)) / 15d).ToImmutableArray() : [];
            if (_rows.Skip(1).Any(row => !row.Definitions.Select(c => c.Right).SequenceEqual(first.Select(c => c.Right))))
                Loss("rtf.row-cell-widths", "Per-row cell widths", "Used the first row's column widths.");
            _section.Add(new Table { Rows = cells.Select(r => r.ToImmutableArray()).ToImmutableArray(), ColumnWidths = widths,
                RowSizing = _rows.Select(r => r.Sizing).ToImmutableArray() });
            _rows.Clear();
        }
        private void EndSection(bool force)
        {
            EndTable();
            if (_section.Count == 0 && (force || _hasPhysicalSections && _blocks.Count == 0)) _section.Add(new Paragraph());
            if (_hasPhysicalSections && _physicalSections.Count > 0 && _section.FirstOrDefault() is Table)
            {
                Loss("rtf.section-table-boundary", "Physical section beginning with a table", "Inserted an empty paragraph before the table to retain the section boundary.");
                _section.Insert(0, new Paragraph());
            }
            if (_section.Count > 0 || force)
            {
                var first = new DocumentIndex(new FlowDocument(_section)).Paragraphs.First().Paragraph.Id;
                _physicalSections.Add(_physicalSection with { StartParagraphId = _physicalSections.Count == 0 ? Guid.Empty : first });
            }
            if (force || _sectionActive && _section.Count > 0) _blocks.Add(new Section { Blocks = _section.Count == 0 ? [new Paragraph()] : _section.ToImmutableArray() });
            else _blocks.AddRange(_section);
            _section.Clear(); _sectionActive = false;
        }
        public FlowDocument Read()
        {
            if (root.Number("ansicpg") is { } codePage)
                try { _ansi = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
                catch (ArgumentException) { Loss("rtf.code-page", "Unsupported ANSI code page", "Decoded ANSI escapes as Windows-1252."); }
            ReadTables();
            if (root.Number("deff") is { } defaultFont)
            {
                if (_fonts.TryGetValue(defaultFont, out var family)) _defaultText = _defaultText with { FontFamily = family };
                else Loss("rtf.undefined-font", "Undefined default font", "Used the model's default font.");
            }
            Walk(root, new(_defaultText, ParagraphStyle.Default), true);
            if (_row is not null) throw new FormatException("Unterminated RTF table row.");
            PendingParagraph(_lastState); EndSection(false);
            var document = new FlowDocument(_blocks) { Resources = _resources.ToImmutable(), Stories = _stories.ToImmutable(), Notes = _notes.ToImmutableArray(),
                Sections = _hasPhysicalSections ? _physicalSections.ToImmutableArray() : [], FootnoteSettings = _footnoteSettings, EndnoteSettings = _endnoteSettings };
            document.Validate(); return document;
        }
        private string DecodeLiterals(List<Node> nodes, ref int index, ref int fallback)
        {
            var literal = (Literal)nodes[index];
            if (!literal.Ansi && !byteSource)
            {
                var skip = Math.Min(fallback, literal.Value.Length); fallback -= skip;
                return literal.Value[skip..];
            }
            using var bytes = new MemoryStream();
            for (; index < nodes.Count && nodes[index] is Literal next && (next.Ansi || byteSource); index++)
            {
                var values = next.Ansi ? next.Value.Select(c => (byte)c).ToArray() : Encoding.GetEncoding(1252).GetBytes(next.Value);
                var skip = Math.Min(fallback, values.Length); fallback -= skip;
                bytes.Write(values, skip, values.Length - skip);
            }
            index--;
            try { return _ansi.GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
            catch (DecoderFallbackException) { throw new FormatException("Invalid byte sequence for the declared RTF code page."); }
        }
        private string Plain(Group group, int unicodeFallback = 1)
        {
            var result = new StringBuilder(); var fallback = 0;
            void Visit(Group value, int unicodeFallback)
            {
                for (var index = 0; index < value.Nodes.Count; index++)
                    switch (value.Nodes[index])
                    {
                        case Group child: Visit(child, unicodeFallback); break;
                        case Literal: result.Append(DecodeLiterals(value.Nodes, ref index, ref fallback)); break;
                        case Control { Word: "uc", Number: { } n }:
                            if (n is < 0 or > 16) throw new FormatException("Unsupported RTF Unicode fallback length.");
                            unicodeFallback = n; break;
                        case Control { Word: "u", Number: { } n }:
                            if (n is < short.MinValue or > ushort.MaxValue) throw new FormatException("Invalid Unicode escape.");
                            result.Append(unchecked((char)n)); fallback = unicodeFallback; break;
                    }
            }
            Visit(group, unicodeFallback); return result.ToString();
        }
        private void ReadTables()
        {
            foreach (var table in root.Groups("fonttbl"))
                foreach (var font in table.Nodes.OfType<Group>())
                    if (font.Number("f") is { } id)
                    {
                        if (_fonts.Count >= 4096) throw new FormatException("RTF contains too many fonts.");
                        _fonts[id] = Plain(font).Trim().TrimEnd(';');
                        if (font.Number("fcharset") is { } charset && charset is not (0 or 1) || font.Number("cpg") is { } page && page != _ansi.CodePage)
                            Loss("rtf.font-code-page", "Font-specific character encoding", "Used the document ANSI code page for byte text.", font.Offset);
                    }
            foreach (var table in root.Groups("colortbl"))
            {
                var red = 0; var green = 0; var blue = 0; var set = false; var first = true;
                foreach (var node in table.Nodes)
                    if (node is Control control)
                    {
                        var n = (int)Range(control.Number ?? 0, 0, 255, "Color component", control.Offset);
                        switch (control.Word) { case "red": red = n; set = true; break; case "green": green = n; set = true; break; case "blue": blue = n; set = true; break; }
                    }
                    else if (node is Literal literal)
                        foreach (var unused in literal.Value.Where(c => c == ';'))
                        {
                            if (_colors.Count >= 4096) throw new FormatException("RTF contains too many colors.");
                            if (first) { _colors[0] = set ? $"#{red:X2}{green:X2}{blue:X2}" : null; first = false; }
                            else _colors.Add(set ? $"#{red:X2}{green:X2}{blue:X2}" : null);
                            red = green = blue = 0; set = false;
                        }
            }
            var definitions = new Dictionary<int, ListDefinition>();
            foreach (var table in root.Groups("listtable"))
                foreach (var list in table.Groups("list"))
                {
                    if (list.Number("listid") is not { } id) throw new FormatException("RTF list definition has no identity.");
                    if (definitions.Count >= 4096) throw new FormatException("RTF contains too many lists.");
                    var levels = list.Groups("listlevel").Select(ReadLevel).ToImmutableArray();
                    if (levels.Length is < 1 or > 9) throw new FormatException("Invalid RTF list level count.");
                    definitions[id] = new() { Levels = levels };
                }
            foreach (var table in root.Groups("listoverridetable"))
                foreach (var item in table.Groups("listoverride"))
                {
                    if (item.Number("ls") is not { } index || item.Number("listid") is not { } id || !definitions.TryGetValue(id, out var definition)) throw new FormatException("Invalid RTF list override.");
                    if (_lists.Count >= 4096) throw new FormatException("RTF contains too many list overrides.");
                    var starts = ImmutableDictionary.CreateBuilder<int, int>(); var level = 0;
                    foreach (var entry in item.Groups("lfolevel"))
                    {
                        if (entry.Number("levelstartat") is { } start) starts[level] = (int)Range(start, 1, 1_000_000, "List start", entry.Offset);
                        if (entry.Groups("listlevel").Any()) Loss("rtf.list-format-override", "Per-instance list format overrides", "Used the base list level format.", entry.Offset);
                        level++;
                    }
                    _lists[index] = new(Guid.NewGuid(), definition, starts.ToImmutable());
                }
        }
        private ListLevelDefinition ReadLevel(Group level)
        {
            var format = level.Number("levelnfc") ?? level.Number("levelnfcn") ?? 0;
            var marker = format switch { 0 => ListMarkerStyle.Decimal, 1 => ListMarkerStyle.UpperRoman, 2 => ListMarkerStyle.LowerRoman,
                3 => ListMarkerStyle.UpperLetter, 4 => ListMarkerStyle.LowerLetter, 23 => ListMarkerStyle.Bullet, _ => ListMarkerStyle.Decimal };
            if (format is not (0 or 1 or 2 or 3 or 4 or 23)) Loss("rtf.list-number-format", $"RTF list number format {format}", "Used decimal numbering.", level.Offset);
            var encoded = level.Groups("leveltext").FirstOrDefault();
            var pattern = encoded is null ? "" : Plain(encoded);
            if (pattern.Length > 0)
            {
                // A byte length precedes the pattern; a literal semicolon can also be part
                // of the pattern, so trimming terminators would corrupt custom suffixes.
                var first = encoded!.Nodes.OfType<Literal>().FirstOrDefault();
                var length = first is { Ansi: true } ? first.Value[0] : pattern[0];
                if (length > pattern.Length - 1) throw new FormatException("Truncated RTF list marker.");
                pattern = pattern.Substring(1, length);
            }
            var slots = pattern.Select((ch, index) => (ch, index)).Where(p => p.ch <= 8).ToArray();
            var prefix = slots.Length == 0 ? "" : pattern[..slots[0].index]; var suffix = slots.Length == 0 ? "." : pattern[(slots[^1].index + 1)..];
            if (marker != ListMarkerStyle.Bullet && (slots.Length == 0 || slots.Select(p => (int)p.ch).Distinct().Count() != slots.Length ||
                slots.Length > 1 && !slots.Select(p => (int)p.ch).SequenceEqual(Enumerable.Range(0, slots.Length))))
                Loss("rtf.list-pattern", "Unrepresentable list marker pattern", "Used the current level's number and supported ancestor ordering.", level.Offset);
            if (slots.Length > 1 && Enumerable.Range(0, slots.Length - 1).Any(i => pattern[(slots[i].index + 1)..slots[i + 1].index] != "."))
                Loss("rtf.list-pattern", "Custom ancestor list separators", "Used dots between ancestor numbers.", level.Offset);
            return new() { Start = (int)Range(level.Number("levelstartat") ?? 1, 1, 1_000_000, "List start", level.Offset), Marker = marker,
                Kind = marker == ListMarkerStyle.Bullet ? ListKind.Bullet : ListKind.Numbered, Text = marker == ListMarkerStyle.Bullet && pattern.Length > 0 ? pattern : null,
                Prefix = prefix, Suffix = suffix, IncludeAncestors = slots.Length > 1 };
        }
        private void Walk(Group group, State state, bool documentRoot = false)
        {
            if (!documentRoot)
            {
                var destination = group.Destination;
                if (destination is "fonttbl" or "colortbl" or "listtable" or "listoverridetable" or "generator" or "info" or "nonesttables") return;
                if (destination is "listtext" or "pntext")
                {
                    if (!_lists.ContainsKey(state.List)) Loss("rtf.orphan-list-marker", "List marker without resolved numbering", "Omitted the literal marker and imported the paragraph as body text.", group.Offset);
                    return;
                }
                if (destination == "field") { Field(group, state); return; }
                if (destination == "pict") { Picture(group, state); return; }
                if (destination is "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf") { HeaderFooter(group); return; }
                if (destination == "footnote") { Note(group, state); return; }
                if (destination is "ftnsep" or "ftnsepc" or "aftnsep" or "aftnsepc")
                {
                    var settings = destination.StartsWith('a') ? _endnoteSettings : _footnoteSettings;
                    var separator = Plain(group);
                    if (separator.Length > 256) { Loss("rtf.note-separator", "Oversized note separator", "Retained the default separator.", group.Offset); return; }
                    settings = destination.EndsWith('c') ? settings with { ContinuationSeparatorText = separator } : settings with { SeparatorText = separator };
                    if (destination.StartsWith('a')) _endnoteSettings = settings; else _footnoteSettings = settings;
                    if (group.Nodes.OfType<Group>().Any() || group.Nodes.OfType<Control>().Any(c => c.Word is "b" or "i" or "pict" or "trowd"))
                        Loss("rtf.note-separator-formatting", "Rich note separator", "Retained the separator text without rich formatting.", group.Offset);
                    return;
                }
                if (destination == "textalonianotemark") return;
                if (destination is "stylesheet" or "object" or "annotation" or "pn" || group.Nodes.FirstOrDefault() is Control { Word: "*" } && destination != "nesttableprops")
                { Loss("rtf.unsupported-destination", $"RTF destination {destination}", "Omitted this destination; retained surrounding body text.", group.Offset); return; }
            }
            var outerProperties = _nestedProperties;
            if (group.Destination == "nesttableprops") _nestedProperties = true;
            for (var index = 0; index < group.Nodes.Count; index++)
                switch (group.Nodes[index])
                {
                    case Group child: Flush(); Walk(child, state); Flush(); break;
                    case Literal: Append(DecodeLiterals(group.Nodes, ref index, ref _fallback), state); break;
                    case Binary binary: Loss("rtf.binary-content", "Binary content outside an image", "Omitted binary data.", binary.Offset); break;
                    case Control control: Flush(); state = Apply(control, state); _lastState = state; break;
                }
            _nestedProperties = outerProperties;
        }
        private DocumentStory ReadStory(Group group, DocumentStoryKind kind)
        {
            if (readingStory) throw new FormatException("Nested RTF stories are unsupported.");
            var nodes = root.Nodes.Where(n => n is Control { Word: "ansicpg" or "deff" } || n is Group { Destination: "fonttbl" or "colortbl" or "listtable" or "listoverridetable" }).ToList();
            nodes.AddRange(group.Nodes.Where(n => n is not Control { Word: "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf" or "footnote" or "ftnalt" or "*" }));
            var storyDocument = new Reader(new Group(nodes, group.Offset), byteSource, true).Read();
            foreach (var resource in storyDocument.Resources)
            {
                _resourceBytes += resource.Value.Data.Length;
                if (_resourceBytes > DocumentResource.MaximumDocumentEmbeddedBytes || _resources.Count >= 4096) throw new FormatException("RTF embedded resources exceed the size limit.");
                _resources.Add(resource.Key, resource.Value);
            }
            var story = new DocumentStory { Kind = kind, Blocks = storyDocument.Blocks };
            if (_stories.Count >= 10000) throw new FormatException("RTF contains too many stories.");
            _stories.Add(story.Id, story);
            return story;
        }
        private void HeaderFooter(Group group)
        {
            if (readingStory) { Loss("rtf.nested-story", "Nested header/footer story", "Omitted the nested destination.", group.Offset); return; }
            var story = ReadStory(group, group.Destination!.StartsWith("header", StringComparison.Ordinal) ? DocumentStoryKind.Header : DocumentStoryKind.Footer);
            var reference = new StoryReference { StoryId = story.Id, LinkToPrevious = false };
            var settings = _physicalSection.HeaderFooter;
            settings = group.Destination switch
            {
                "headerf" => settings with { FirstHeader = reference }, "headerl" => settings with { EvenHeader = reference },
                "footerf" => settings with { FirstFooter = reference }, "footerl" => settings with { EvenFooter = reference },
                "footer" or "footerr" => settings with { PrimaryFooter = reference }, _ => settings with { PrimaryHeader = reference }
            };
            _physicalSection = _physicalSection with { HeaderFooter = settings };
            _hasPhysicalSections = true;
        }
        private void Note(Group group, State state)
        {
            if (readingStory) { Loss("rtf.nested-note", "Note reference in a secondary story", "Omitted the nested note destination.", group.Offset); return; }
            var endnote = group.Nodes.OfType<Control>().Any(c => c.Word == "ftnalt");
            var custom = group.Groups("textalonianotemark").FirstOrDefault();
            var mark = custom is null ? null : Plain(custom);
            var markerGroup = group.Nodes.OfType<Group>().FirstOrDefault(g => g.Destination == "super");
            if (mark is null && markerGroup is not null && !group.Nodes.OfType<Control>().Any(c => c.Word == "chftn"))
                mark = Plain(markerGroup);
            if (mark is not null && (string.IsNullOrWhiteSpace(mark) || mark.Length > 32 || mark.Any(char.IsControl)))
            { Loss("rtf.custom-note-mark", "Unsupported custom note mark", "Retained marker text and used automatic note numbering.", group.Offset); mark = null; }
            if (mark is not null)
            {
                Flush();
                if (_runs.LastOrDefault() is { Inline: null } last && last.Text.EndsWith(mark, StringComparison.Ordinal))
                {
                    _runs.RemoveAt(_runs.Count - 1);
                    if (last.Text.Length > mark.Length) _runs.Add(new RichRun(last.Text[..^mark.Length], last.Style));
                }
                else Loss("rtf.custom-note-mark", "Detached custom note marker", "Retained surrounding body text and attached the custom mark to the note reference.", group.Offset);
                if (markerGroup is not null && Plain(markerGroup) == mark)
                    group = group with { Nodes = group.Nodes.Where(node => !ReferenceEquals(node, markerGroup)).ToList() };
            }
            var story = ReadStory(group, endnote ? DocumentStoryKind.Endnote : DocumentStoryKind.Footnote);
            var note = new DocumentNote { StoryId = story.Id, Kind = endnote ? DocumentNoteKind.Endnote : DocumentNoteKind.Footnote, CustomMark = mark };
            _notes.Add(note); Flush();
            _runs.Add(new RichRun(InlineDescriptor.Note(note.Id, mark ?? "1"), state.Text));
        }
        private State Apply(Control control, State state)
        {
            var number = control.Number ?? 0; var enabled = control.Number is null || number != 0;
            switch (control.Word)
            {
                case "*": case "nesttableprops": case "rtf": case "ansi": case "ansicpg": case "deff": case "deflang": case "viewkind": case "viewscale": case "fet": case "formshade":
                case "fldrslt": case "intbl": case "lang": case "langfe": case "loch": case "hich": case "dbch": break;
                case "chftn": break; // The adjacent footnote destination supplies the atomic body marker.
                case "facingp": _oddEvenHeaders = enabled; _hasPhysicalSections = true; _physicalSection = _physicalSection with { HeaderFooter = _physicalSection.HeaderFooter with { DifferentOddEvenPages = enabled } }; break;
                case "titlepg": _hasPhysicalSections = true; _physicalSection = _physicalSection with { HeaderFooter = _physicalSection.HeaderFooter with { DifferentFirstPage = enabled } }; break;
                case "headery": _hasPhysicalSections = true; _physicalSection = _physicalSection with { HeaderFooter = _physicalSection.HeaderFooter with { HeaderDistance = Range(number / 15d, 0, 10000, "Header distance", control.Offset) } }; break;
                case "footery": _hasPhysicalSections = true; _physicalSection = _physicalSection with { HeaderFooter = _physicalSection.HeaderFooter with { FooterDistance = Range(number / 15d, 0, 10000, "Footer distance", control.Offset) } }; break;
                case "sbknone": _physicalSection = _physicalSection with { BreakKind = SectionBreakKind.Continuous }; break;
                case "sbkpage": _physicalSection = _physicalSection with { BreakKind = SectionBreakKind.NextPage }; break;
                case "sbkodd": _physicalSection = _physicalSection with { BreakKind = SectionBreakKind.OddPage }; break;
                case "sbkeven": _physicalSection = _physicalSection with { BreakKind = SectionBreakKind.EvenPage }; break;
                case "sbkcol": _physicalSection = _physicalSection with { BreakKind = SectionBreakKind.NextColumn }; break;
                case "ftnstart": case "aftnstart": case "ftnrstpg": case "ftnrestart": case "ftnrstcont": case "aftnrestart": case "aftnrstcont":
                case "ftnnar": case "ftnnalc": case "ftnnauc": case "ftnnrlc": case "ftnnruc": case "aftnnar": case "aftnnalc": case "aftnnauc": case "aftnnrlc": case "aftnnruc":
                case "ftnbj": case "ftntj": case "aenddoc": case "aendnotes": case "enddoc": case "endnotes":
                    NoteSetting(control); break;
                case "trgaph": case "trleft":
                    if (number != 0) Loss("rtf.table-spacing", "RTF table offset or cell gap", "Used the model's default table spacing.", control.Offset);
                    break;
                case "slmult":
                    if (number != 0) Loss("rtf.relative-line-height", "Multiple line spacing", "Interpreted the line spacing value as an absolute height.", control.Offset);
                    break;
                case "par": Paragraph(state); break;
                case "line": Append("\u2028", state); break;
                case "tab": Append("\t", state); break;
                case "~": Append("\u00a0", state); break;
                case "_": Append("\u2011", state); break;
                case "-": Append("\u00ad", state); break;
                case "emdash": Append("\u2014", state); break;
                case "endash": Append("\u2013", state); break;
                case "lquote": Append("\u2018", state); break;
                case "rquote": Append("\u2019", state); break;
                case "ldblquote": Append("\u201c", state); break;
                case "rdblquote": Append("\u201d", state); break;
                case "bullet": Append("\u2022", state); break;
                case "u":
                    if (control.Number is null || number is < short.MinValue or > ushort.MaxValue) throw new FormatException("Invalid Unicode escape.");
                    _fallback = 0; Append(unchecked((char)number).ToString(), state); _fallback = state.UnicodeFallback; break;
                case "uc":
                    if (control.Number is null || number is < 0 or > 16) throw new FormatException("Unsupported RTF Unicode fallback length.");
                    return state with { UnicodeFallback = number };
                case "b": return state with { Text = state.Text with { Bold = enabled } };
                case "i": return state with { Text = state.Text with { Italic = enabled } };
                case "ul": return state with { Text = state.Text with { Underline = enabled } };
                case "ulnone": return state with { Text = state.Text with { Underline = false } };
                case "strike": return state with { Text = state.Text with { Strikethrough = enabled } };
                case "sub": return state with { Text = state.Text with { Baseline = Baseline.Subscript } };
                case "super": return state with { Text = state.Text with { Baseline = Baseline.Superscript } };
                case "nosupersub": return state with { Text = state.Text with { Baseline = Baseline.Normal } };
                case "fs": return state with { Text = state.Text with { FontSize = Range(number * 2d / 3, 1, 512, "Font size", control.Offset) } };
                case "f":
                    if (!_fonts.ContainsKey(number)) Loss("rtf.undefined-font", "Undefined font reference", "Used the model's default font.", control.Offset);
                    return state with { Text = state.Text with { FontFamily = _fonts.GetValueOrDefault(number) } };
                case "cf":
                    if (number < 0 || number >= _colors.Count) Loss("rtf.undefined-color", "Undefined foreground color", "Used the inherited foreground color.", control.Offset);
                    return state with { Text = state.Text with { Foreground = Color(number) } };
                case "highlight": case "chcbpat":
                    if (number < 0 || number >= _colors.Count) Loss("rtf.undefined-color", "Undefined background color", "Used the inherited background color.", control.Offset);
                    return state with { Text = state.Text with { Background = Color(number) } };
                case "charscalex":
                    var stretch = Enumerable.Range(1, 9).MinBy(s => Math.Abs(StretchPercent(s) - number));
                    if (StretchPercent(stretch) != number) Loss("rtf.font-stretch", "Arbitrary character scaling", "Used the nearest font width class.", control.Offset);
                    return state with { Text = state.Text with { FontStretch = stretch } };
                case "plain": return state with { Text = _defaultText with { Hyperlink = state.Text.Hyperlink } };
                case "pard":
                    if (_row is null) EndTable();
                    return state with { Paragraph = ParagraphStyle.Default, List = 0 };
                case "ql": return state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Left } };
                case "qc": return state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Center } };
                case "qr": return state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Right } };
                case "qj": return state with { Paragraph = state.Paragraph with { Alignment = ParagraphAlignment.Justify } };
                case "rtlpar": return state with { Paragraph = state.Paragraph with { RightToLeft = true } };
                case "ltrpar": return state with { Paragraph = state.Paragraph with { RightToLeft = false } };
                case "li": return state with { Paragraph = state.Paragraph with { Indent = Range(number / 15d, 0, 1000, "Left indent", control.Offset) } };
                case "ri": return state with { Paragraph = state.Paragraph with { RightIndent = Range(number / 15d, 0, 100000, "Right indent", control.Offset) } };
                case "fi": return state with { Paragraph = state.Paragraph with { FirstLineIndent = Range(number / 15d, -100000, 100000, "First line indent", control.Offset) } };
                case "sb": return state with { Paragraph = state.Paragraph with { SpaceBefore = Range(number / 15d, 0, 1000, "Space before", control.Offset) } };
                case "sa": return state with { Paragraph = state.Paragraph with { SpaceAfter = Range(number / 15d, 0, 1000, "Space after", control.Offset) } };
                case "sl":
                    if (number > 0) Loss("rtf.minimum-line-height", "Minimum line height", "Used an exact line height.", control.Offset);
                    return state with { Paragraph = state.Paragraph with { LineHeight = number == 0 ? null : Range(Math.Abs(number / 15d), 1d / 15, 10000, "Line height", control.Offset) } };
                case "expndtw":
                    if (_runs.Count > 0 && state.Paragraph.LetterSpacing != number / 15d) Loss("rtf.character-spacing", "Different character spacing within a paragraph", "Applied the last character spacing to the whole paragraph.", control.Offset);
                    return state with { Paragraph = state.Paragraph with { LetterSpacing = Range(number / 15d, -1000, 1000, "Letter spacing", control.Offset) } };
                case "outlinelevel":
                    if (number is >= 6 and < 9) Loss("rtf.heading-level", "Heading level above six", "Imported the paragraph as ordinary body text.", control.Offset);
                    return state with { Paragraph = state.Paragraph with { HeadingLevel = number is >= 0 and < 6 ? number + 1 : 0 } };
                case "ls": return state with { List = number };
                case "ilvl":
                    if (number is < 0 or > 8) throw new FormatException("Invalid RTF list level.");
                    return state with { Paragraph = state.Paragraph with { ListLevel = number } };
                case "sectd": PendingParagraph(state); if (_section.Count > 0 || _rows.Count > 0) EndSection(false); _sectionActive = true;
                    _physicalSection = new() { HeaderFooter = new() { DifferentOddEvenPages = _oddEvenHeaders } }; break;
                case "sect": PendingParagraph(state); EndSection(true); _sectionActive = true;
                    _physicalSection = _physicalSection with { Id = Guid.NewGuid(), HeaderFooter = new() { DifferentOddEvenPages = _oddEvenHeaders,
                        HeaderDistance = _physicalSection.HeaderFooter.HeaderDistance, FooterDistance = _physicalSection.HeaderFooter.FooterDistance } }; break;
                case "trowd":
                    if (_nestedProperties)
                    {
                        if (_row is null || _outerRows.Count == 0) throw new FormatException("Nested row properties have no nested row.");
                        _row.Definitions.Clear(); _cell = new();
                    }
                    else if (_row is not null) NestedRow(state, control.Offset);
                    else { PendingParagraph(state); _row = new(); _cell = new(); }
                    break;
                case "itap":
                    var depth = _row is null ? 0 : _outerRows.Count + 1;
                    if (number == depth + 1 && _row is not null) NestedRow(state, control.Offset);
                    else if (number != depth && !(number == 1 && _row is null)) throw new FormatException("Invalid RTF table nesting level.");
                    break;
                case "cellx":
                    if (_row is null || _row.Definitions.Count >= 100 || number <= (_row.Definitions.LastOrDefault()?.Right ?? 0)) throw new FormatException("Invalid RTF cell boundary.");
                    _row.Definitions.Add(_cell with { Right = number }); _cell = new(); break;
                case "clcbpat": _cell = _cell with { Background = Color(number) }; break;
                case "clmgf": _cell = _cell with { HorizontalStart = true }; break;
                case "clmrg": _cell = _cell with { HorizontalContinue = true }; break;
                case "clvmgf": _cell = _cell with { VerticalStart = true }; break;
                case "clvmrg": _cell = _cell with { VerticalContinue = true }; break;
                case "trrh":
                    if (_row is not null) _row.Sizing = new() { Mode = number == 0 ? TableRowHeightMode.Auto : number < 0 ? TableRowHeightMode.Exact : TableRowHeightMode.AtLeast, Height = Math.Abs(number / 15d) };
                    break;
                case "cell": case "nestcell": EndCell(state); break;
                case "nestrow":
                    if (_outerRows.Count == 0) throw new FormatException("Nested row terminator outside a nested table.");
                    EndRow(); break;
                case "row": EndRow(); break;
                default: Loss("rtf.unsupported-control", $"RTF control \\{control.Word}", "Ignored this control and retained its text.", control.Offset); break;
            }
            return state;
        }
        private void NoteSetting(Control control)
        {
            var word = control.Word; var endnote = word.StartsWith('a');
            if (word is "endnotes" or "enddoc") endnote = true;
            var settings = endnote ? _endnoteSettings : _footnoteSettings;
            if (word.StartsWith('a')) word = word[1..];
            settings = word switch
            {
                "ftnstart" => settings with { Start = (int)Range(control.Number ?? 1, 1, 1000000, "Note starting number", control.Offset) },
                "ftnrstpg" => settings with { Restart = NoteRestartPolicy.EachPage },
                "ftnrestart" => settings with { Restart = NoteRestartPolicy.EachSection },
                "ftnrstcont" => settings with { Restart = NoteRestartPolicy.Continuous },
                "ftnnalc" => settings with { NumberFormat = PageNumberFormat.LowerLetter }, "ftnnauc" => settings with { NumberFormat = PageNumberFormat.UpperLetter },
                "ftnnrlc" => settings with { NumberFormat = PageNumberFormat.LowerRoman }, "ftnnruc" => settings with { NumberFormat = PageNumberFormat.UpperRoman },
                "ftnnar" => settings with { NumberFormat = PageNumberFormat.Decimal },
                "ftntj" => settings with { Placement = NotePlacement.BelowText }, "ftnbj" => settings with { Placement = NotePlacement.PageBottom },
                "endnotes" => settings with { Placement = NotePlacement.SectionEnd }, "enddoc" => settings with { Placement = NotePlacement.DocumentEnd }, _ => settings
            };
            if (endnote) _endnoteSettings = settings; else _footnoteSettings = settings;
        }
        private void Field(Group group, State state)
        {
            static IEnumerable<Node> Descendants(Group parent)
            {
                foreach (var node in parent.Nodes)
                {
                    yield return node;
                    if (node is Group child) foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }
            var instructions = group.Groups("fldinst").ToArray();
            var instruction = instructions.Length == 1 ? instructions[0] : null;
            var value = instruction is null ? "" : Plain(instruction, state.UnicodeFallback).Trim();
            var results = group.Groups("fldrslt").ToArray();
            var nested = Descendants(group).OfType<Group>().Any(g => g.Destination == "field");
            var structured = results.SelectMany(Descendants).OfType<Control>().Any(c => c.Word is "par" or "pard" or "sectd" or "sect" or "cell" or "row" or "trowd" or "nestcell" or "nestrow" or "itap" or "nesttableprops" or "cellx" or "clcbpat" or "clmgf" or "clmrg" or "clvmgf" or "clvmrg" or "trrh");
            var parsed = MergeFieldInstructions.Parse(value);
            if (!_flattenFields && !nested && !structured && results.Length <= 1 && PageFieldInstructions.TryParse(value, out var pageField))
            {
                Flush(); var start = _runs.Count;
                foreach (var result in results) Walk(result, state);
                Flush();
                var cachedRuns = _runs.Skip(start).ToArray();
                var cached = string.Concat(cachedRuns.Select(r => r.PlainText));
                if (cachedRuns.Any(r => r.Inline is not null) || cached.Length > 16384)
                { Loss("rtf.field-result", "Non-text or oversized page-field result", "Retained the result content without active field semantics.", group.Offset); return; }
                _runs.RemoveRange(start, _runs.Count - start);
                var style = cachedRuns.FirstOrDefault()?.Style ?? state.Text;
                if (cachedRuns.Any(r => r.Style != style))
                    Loss("rtf.field-result-formatting", "Multiple styles in an atomic page field", "Applied the first character style to the field display.", group.Offset);
                _runs.Add(new RichRun(new InlineDescriptor { Payload = new PageFieldInlinePayload(pageField), AltText = results.Length == 0 ? "1" : cached }, style));
                return;
            }
            if (nested) Loss("rtf.nested-field", "Nested RTF fields", "Retained visible results without active field semantics.", group.Offset);
            if (results.Length > 1) Loss("rtf.field-structure", "Multiple field results", "Retained visible results without active field semantics.", group.Offset);
            if (parsed is not null && !_flattenFields && !nested && !structured && results.Length <= 1)
            {
                Flush(); var start = _runs.Count;
                foreach (var result in results) Walk(result, state);
                Flush();
                var cachedRuns = _runs.Skip(start).ToArray();
                var cached = string.Concat(cachedRuns.Select(r => r.PlainText));
                if (cachedRuns.Any(r => r.Inline is not null) || cached.Length > 16_384)
                {
                    Loss("rtf.field-result", "Non-text or oversized field result", "Retained the result content without active field semantics.", group.Offset);
                    return;
                }
                _runs.RemoveRange(start, _runs.Count - start);
                var style = cachedRuns.FirstOrDefault()?.Style ?? (results.Length == 0 ? state.Text : _lastState.Text);
                if (cachedRuns.Any(r => r.Style != style))
                    Loss("rtf.field-result-formatting", "Multiple styles in an atomic merge field", "Applied the first character style to the field display.", group.Offset);
                if (parsed.UnsupportedSwitches || parsed.CharacterFormat)
                    Loss("rtf.merge-field-switch", "Unsupported merge-field switches", "Retained the field name and cached display style without the switch behavior.", group.Offset);
                _runs.Add(MergeFieldInstructions.Create(parsed.Name, cached, style));
                return;
            }
            if (structured && parsed is not null)
                Loss("rtf.field-result", "Block structure in a merge-field result", "Retained the result content without active field semantics.", group.Offset);
            var match = Regex.Match(value, "^HYPERLINK\\s+(?:\"([^\"]*)\"|(\\S+))\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var link = match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
            if (!_flattenFields && !nested && link is not null && FlowDocument.IsSafeHyperlink(link)) state = state with { Text = state.Text with { Hyperlink = link } };
            else Loss(link is null || _flattenFields || nested ? "rtf.unsupported-field" : "rtf.unsafe-link", link is null || _flattenFields || nested ? "RTF field instruction" : "Unsafe hyperlink", "Retained the visible field result without an active link.", group.Offset);
            var previous = _flattenFields;
            _flattenFields = true;
            try { foreach (var result in results) Walk(result, state); }
            finally { _flattenFields = previous; }
        }
        private void Picture(Group group, State state)
        {
            var mediaType = group.Nodes.OfType<Control>().Any(c => c.Word == "pngblip") ? "image/png" : group.Nodes.OfType<Control>().Any(c => c.Word == "jpegblip") ? "image/jpeg" : null;
            if (mediaType is null) { Loss("rtf.unsupported-image", "RTF image encoding", "Replaced the image with [image].", group.Offset); Append("[image]", state); return; }
            var binaries = group.Nodes.OfType<Binary>().ToArray(); byte[] bytes;
            if (binaries.Length > 0)
            {
                if (binaries.Sum(b => (long)b.Value.Length) > DocumentResource.MaximumEmbeddedBytes) throw new FormatException("RTF image is too large.");
                bytes = binaries.SelectMany(b => Encoding.GetEncoding(1252).GetBytes(b.Value)).ToArray();
            }
            else
            {
                var hex = string.Concat(string.Concat(group.Nodes.OfType<Literal>().Select(l => l.Value)).Where(c => !char.IsWhiteSpace(c)));
                if (hex.Length > DocumentResource.MaximumEmbeddedBytes * 2) throw new FormatException("RTF image is too large.");
                try { bytes = Convert.FromHexString(hex); } catch (FormatException) { throw new FormatException("Invalid RTF image data."); }
            }
            if (bytes.Length == 0 || bytes.Length > DocumentResource.MaximumEmbeddedBytes) throw new FormatException("Invalid RTF image size.");
            _resourceBytes += bytes.Length;
            if (_resourceBytes > DocumentResource.MaximumDocumentEmbeddedBytes || _resources.Count >= 4096) throw new FormatException("RTF embedded resources exceed the size limit.");
            var id = $"rtf-image-{_resourcePrefix}-{_resources.Count + 1}";
            _resources.Add(id, new() { Kind = DocumentResourceKind.Embedded, MediaType = mediaType, Data = bytes.ToImmutableArray() });
            var width = group.Number("picwgoal") is { } w ? w / 15d : group.Number("picw") ?? 32;
            var height = group.Number("pichgoal") is { } h ? h / 15d : group.Number("pich") ?? 32;
            Flush(); _runs.Add(new(new InlineDescriptor { Payload = new ImageInlinePayload(id), Width = Range(width, 1d / 15, 10000, "Image width", group.Offset), Height = Range(height, 1d / 15, 10000, "Image height", group.Offset) }, state.Text));
        }
    }

    private sealed record ExportList(int Id, ListDefinition Definition, int Start, int Level, ImmutableDictionary<int, int> Ancestors);
    public override string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        StyleConversion.ReportLosses(this, document);
        document = new DocumentStyleResolver(document).ResolveDocument();
        document.Validate();
        var foundSection = false;
        foreach (var block in document.Blocks)
            if (block is Section) foundSection = true;
            else if (foundSection)
            {
                ConversionDiagnostics.Report("rtf.section-grouping", "Root blocks following a flow section", "Grouped the following root blocks into the next RTF section.", block.Id);
                break;
            }
        var storyDocuments = document.Stories.Values.Select(story => new FlowDocument(story.Blocks)).ToArray();
        var positions = new DocumentIndex(document).Paragraphs.Concat(storyDocuments.SelectMany(story => new DocumentIndex(story).Paragraphs)).ToArray();
        var paragraphDocuments = storyDocuments.Prepend(document).SelectMany(owner => new DocumentIndex(owner).Paragraphs.Select(p => (p.Paragraph.Id, Owner: owner))).ToDictionary(p => p.Id, p => p.Owner);
        var paragraphs = positions.Select(p => p.Paragraph).ToArray();
        var styles = paragraphs.SelectMany(p => p.Runs.Select(r => r.Style).Append(p.DefaultStyle)).ToArray();
        var fonts = styles.Select(s => s.FontFamily ?? "Arial").Distinct().ToArray();
        var colors = styles.SelectMany(s => new[] { s.Foreground, s.Background }).Concat(AllCells(document.Blocks.Concat(document.Stories.Values.SelectMany(s => s.Blocks))).Select(c => c.Background)).Where(c => c is not null).Distinct().ToArray();
        foreach (var color in colors.Where(c => c!.Length == 9 && !c.StartsWith("#FF", StringComparison.OrdinalIgnoreCase)))
            ConversionDiagnostics.Report("rtf.color-alpha", "Color transparency", "Exported the opaque RGB color.");
        int ColorIndex(string? color) => color is null ? 0 : Array.IndexOf(colors, color) + 1;
        void Precision(Guid id, params double[] values)
        {
            if (values.Any(value => Math.Abs(value * 15 - Math.Round(value * 15)) > 0.00001))
                ConversionDiagnostics.Report("rtf.dimension-precision", "Fractional dimensions", "Rounded dimensions to RTF twips (1/15 device-independent pixel).", id);
        }
        var lists = new List<ExportList>(); var paragraphLists = new Dictionary<Guid, int>(); var activeLists = new Dictionary<Guid, int>();
        var anonymous = new Dictionary<Guid, Guid>(); var definitions = new Dictionary<Guid, ListDefinition>();
        var counters = new Dictionary<Guid, int?[]>();
        foreach (var position in positions)
        {
            var p = position.Paragraph;
            if (p.Style.List == ListKind.None) { anonymous.Remove(position.ContainerId); continue; }
            if (!anonymous.TryGetValue(position.ContainerId, out var sequence)) anonymous[position.ContainerId] = sequence = Guid.NewGuid();
            var identity = p.Style.ListId ?? (p.Style.List == ListKind.Bullet ? Guid.NewGuid() : sequence);
            var inherited = p.Style.ListDefinition ?? definitions.GetValueOrDefault(identity) ?? new ListDefinition();
            definitions[identity] = inherited;
            var definition = new ListDefinition { Levels = Enumerable.Range(0, 9).Select(level => inherited.Level(level, p.Style.List)).ToImmutableArray() };
            if (!counters.TryGetValue(identity, out var values)) counters[identity] = values = new int?[9];
            for (var i = 0; i < p.Style.ListLevel; i++) values[i] ??= definition.Level(i, p.Style.List).Start;
            values[p.Style.ListLevel] = ListNumbering.GetMarker(paragraphDocuments[p.Id], p.Id)!.Number;
            for (var i = p.Style.ListLevel + 1; i < values.Length; i++) values[i] = null;
            if (!activeLists.TryGetValue(identity, out var current) || p.Style.ListRestart || p.Style.ListStart is not null || !lists[current - 1].Definition.Equals(definition))
            {
                if (current != 0) ConversionDiagnostics.Report("rtf.list-instance", "List restart or format change", "Started a new RTF list instance; visible numbering is preserved.", p.Id);
                var ancestors = Enumerable.Range(0, p.Style.ListLevel).ToImmutableDictionary(i => i, i => values[i]!.Value);
                lists.Add(new(lists.Count + 1, definition, values[p.Style.ListLevel]!.Value, p.Style.ListLevel, ancestors)); activeLists[identity] = current = lists.Count;
            }
            paragraphLists[p.Id] = current;
        }
        var b = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl");
        for (var i = 0; i < fonts.Length; i++) b.Append("{\\f").Append(i).Append(' ').Append(Escape(fonts[i].Replace(";", ""))).Append(";}");
        b.Append("}{\\colortbl ;");
        foreach (var color in colors)
        {
            var rgb = color![^6..];
            b.Append("\\red").Append(Convert.ToInt32(rgb[..2], 16)).Append("\\green").Append(Convert.ToInt32(rgb[2..4], 16)).Append("\\blue").Append(Convert.ToInt32(rgb[4..], 16)).Append(';');
        }
        b.Append('}'); WriteLists(b, lists);
        var writingStory = false;
        var exportedStories = new HashSet<Guid>();
        var sectionStarts = document.Sections.Skip(1).ToDictionary(section => section.StartParagraphId);
        void WriteNoteSettings(NoteSettings settings, bool endnote)
        {
            var prefix = endnote ? "aftn" : "ftn";
            b.Append('\\').Append(prefix).Append("start").Append(settings.Start).Append('\\').Append(prefix)
                .Append(settings.Restart switch { NoteRestartPolicy.EachPage => "rstpg", NoteRestartPolicy.EachSection => "restart", _ => "rstcont" })
                .Append('\\').Append(prefix).Append(settings.NumberFormat switch { PageNumberFormat.UpperRoman => "nruc", PageNumberFormat.LowerRoman => "nrlc", PageNumberFormat.UpperLetter => "nauc", PageNumberFormat.LowerLetter => "nalc", _ => "nar" })
                .Append(settings.Placement switch { NotePlacement.BelowText => "\\ftntj", NotePlacement.SectionEnd => "\\aendnotes", NotePlacement.DocumentEnd => "\\aenddoc", _ => "\\ftnbj" });
            b.Append("{\\*\\").Append(prefix).Append("sep ").Append(Escape(settings.SeparatorText)).Append('}')
                .Append("{\\*\\").Append(prefix).Append("sepc ").Append(Escape(settings.ContinuationSeparatorText)).Append('}');
        }
        WriteNoteSettings(document.FootnoteSettings, false);
        WriteNoteSettings(document.EndnoteSettings, true);
        if (document.Sections.Any(section => section.HeaderFooter.DifferentOddEvenPages)) b.Append("\\facingp ");
        void WriteStory(DocumentStory story)
        {
            exportedStories.Add(story.Id);
            var previous = writingStory; writingStory = true;
            try { WriteBlocks(story.Blocks); }
            finally { writingStory = previous; }
        }
        void WritePhysicalSection(DocumentSection section)
        {
            var settings = section.HeaderFooter;
            Precision(section.Id, settings.HeaderDistance, settings.FooterDistance);
            b.Append("\\sectd").Append(section.BreakKind switch { SectionBreakKind.Continuous => "\\sbknone", SectionBreakKind.OddPage => "\\sbkodd", SectionBreakKind.EvenPage => "\\sbkeven", SectionBreakKind.NextColumn => "\\sbkcol", _ => "\\sbkpage" })
                .Append("\\headery").Append(Twips(settings.HeaderDistance)).Append("\\footery").Append(Twips(settings.FooterDistance)).Append(settings.DifferentFirstPage ? "\\titlepg" : "\\titlepg0").Append(' ');
            if (!settings.DifferentOddEvenPages && document.Sections.Any(s => s.HeaderFooter.DifferentOddEvenPages))
                ConversionDiagnostics.Report("rtf.section-odd-even", "Section-specific odd/even header option", "RTF uses a document-wide odd/even header option.", section.Id);
            if (section.PageSettings != new PageSettings() || section.PageNumberStart is not null || section.PageNumberFormat != PageNumberFormat.Decimal)
                ConversionDiagnostics.Report("rtf.physical-page-settings", "Physical page metrics and page numbering", "Retained section boundaries and header/footer settings using default page metrics and numbering.", section.Id);
            foreach (var item in new[] { ("header", settings.PrimaryHeader), ("headerf", settings.FirstHeader), ("headerl", settings.EvenHeader), ("footer", settings.PrimaryFooter), ("footerf", settings.FirstFooter), ("footerl", settings.EvenFooter) })
            {
                if (item.Item2.LinkToPrevious) continue;
                b.Append("{\\").Append(item.Item1).Append(' ');
                if (item.Item2.StoryId is { } id) WriteStory(document.Stories[id]);
                b.Append('}');
            }
        }
        void CharacterStyle(TextStyle s, Guid id)
        {
            if (s.FontFamily is { } family && (family.Contains(';') || family.Trim() != family))
                ConversionDiagnostics.Report("rtf.font-family", "Font family containing semicolons or surrounding whitespace", "Removed semicolons and trimmed the RTF font name.", id);
            if (s.FontWeight is not null) ConversionDiagnostics.Report("rtf.font-weight", "Numeric font weight", "Used regular or bold character formatting.", id);
            if (Math.Abs(s.FontSize * 1.5 - Math.Round(s.FontSize * 1.5)) > 0.00001) ConversionDiagnostics.Report("rtf.font-size-precision", "Fractional font size", "Rounded to an RTF half point.", id);
            b.Append("\\plain\\f").Append(Array.IndexOf(fonts, s.FontFamily ?? "Arial")).Append("\\fs").Append((int)Math.Round(s.FontSize * 1.5))
                .Append(s.EffectiveBold ? "\\b" : "").Append(s.Italic ? "\\i" : "").Append(s.Underline ? "\\ul" : "").Append(s.Strikethrough ? "\\strike" : "")
                .Append(s.Baseline switch { Baseline.Subscript => "\\sub", Baseline.Superscript => "\\super", _ => "" })
                .Append("\\charscalex").Append(StretchPercent(s.FontStretch)).Append("\\cf").Append(ColorIndex(s.Foreground)).Append("\\highlight").Append(ColorIndex(s.Background)).Append(' ');
        }
        void WriteParagraph(Paragraph p, bool inTable)
        {
            if (!writingStory && !inTable && sectionStarts.TryGetValue(p.Id, out var physicalSection))
            { b.Append("\\sect\n"); WritePhysicalSection(physicalSection); }
            Precision(p.Id, p.Style.Indent, p.Style.RightIndent, p.Style.FirstLineIndent, p.Style.SpaceBefore, p.Style.SpaceAfter, p.Style.LetterSpacing, p.Style.LineHeight ?? 0);
            b.Append("\\pard").Append(inTable ? "\\intbl" : "").Append(p.Style.Alignment switch { ParagraphAlignment.Center => "\\qc", ParagraphAlignment.Right => "\\qr", ParagraphAlignment.Justify => "\\qj", _ => "\\ql" });
            b.Append(p.Style.RightToLeft ? "\\rtlpar" : "\\ltrpar").Append("\\li").Append(Twips(p.Style.Indent)).Append("\\ri").Append(Twips(p.Style.RightIndent)).Append("\\fi").Append(Twips(p.Style.FirstLineIndent))
                .Append("\\sb").Append(Twips(p.Style.SpaceBefore)).Append("\\sa").Append(Twips(p.Style.SpaceAfter)).Append("\\expndtw").Append(Twips(p.Style.LetterSpacing))
                .Append("\\sl").Append(p.Style.LineHeight is { } lineHeight ? -Math.Max(1, Twips(lineHeight)) : 0).Append("\\slmult0");
            if (p.Style.HeadingLevel > 0) b.Append("\\outlinelevel").Append(p.Style.HeadingLevel - 1);
            if (paragraphLists.TryGetValue(p.Id, out var list)) b.Append("\\ls").Append(list).Append("\\ilvl").Append(p.Style.ListLevel);
            b.Append(' '); CharacterStyle(p.DefaultStyle, p.Id);
            foreach (var run in p.Runs)
            {
                if (run.Inline is { Payload: NoteInlinePayload notePayload } noteInline)
                {
                    var note = document.Notes.Single(n => n.Id == notePayload.NoteId);
                    if (writingStory)
                    {
                        ConversionDiagnostics.Report("rtf.nested-note", "Note reference in a secondary story", "Retained the reference display without its note story.", noteInline.Id);
                        b.Append(Escape(note.CustomMark ?? noteInline.AltText)); continue;
                    }
                    b.Append('{'); CharacterStyle(run.Style, noteInline.Id);
                    if (note.CustomMark is null) b.Append("\\chftn ");
                    else b.Append("{\\super ").Append(Escape(note.CustomMark)).Append('}');
                    b.Append("{\\footnote").Append(note.Kind == DocumentNoteKind.Endnote ? "\\ftnalt " : " ");
                    if (note.CustomMark is { } custom)
                        b.Append("{\\*\\textalonianotemark ").Append(Escape(custom)).Append("}{\\super ").Append(Escape(custom)).Append('}');
                    else b.Append("\\chftn ");
                    WriteStory(document.Stories[note.StoryId]); b.Append("}}");
                    continue;
                }
                if (run.Inline is { Payload: PageFieldInlinePayload pageField } pageInline)
                {
                    b.Append("{\\field{\\*\\fldinst ").Append(pageField.Field.ToString().ToUpperInvariant()).Append("}{\\fldrslt {");
                    CharacterStyle(run.Style, pageInline.Id); b.Append(Escape(pageInline.AltText)).Append("}}}"); continue;
                }
                if (run.Inline is { Payload: MergeFieldInlinePayload field } fieldInline)
                {
                    MergeFieldInstructions.ReportExportOptions("rtf", fieldInline, field);
                    if (run.Style.Hyperlink is not null)
                        ConversionDiagnostics.Report("rtf.merge-field-hyperlink", "Hyperlinked merge field", "Retained the merge field and display formatting without hyperlink navigation.", fieldInline.Id);
                    b.Append("{\\field{\\*\\fldinst ").Append(Escape(MergeFieldInstructions.Write(field.Name))).Append("}{\\fldrslt {");
                    CharacterStyle(run.Style, p.Id);
                    b.Append(Escape(fieldInline.AltText)).Append("}}}");
                    continue;
                }
                if (run.Style.Hyperlink is { } link) b.Append("{\\field{\\*\\fldinst HYPERLINK \"").Append(Escape(link.Replace("\"", "%22"))).Append("\"}{\\fldrslt ");
                b.Append('{'); CharacterStyle(run.Style, p.Id);
                if (run.Inline is { } inline)
                {
                    if (inline.Payload is ImageInlinePayload image && document.Resources.TryGetValue(image.ResourceId, out var resource) && resource.Kind == DocumentResourceKind.Embedded && !resource.Data.IsEmpty && resource.MediaType is "image/png" or "image/jpeg")
                    {
                        Precision(inline.Id, inline.Width, inline.Height);
                        if (inline.AltText.Length > 0) ConversionDiagnostics.Report("rtf.image-alt-text", "Image alternative text", "Retained the image; RTF picture data omits its alternative text.", inline.Id);
                        b.Append("{\\pict").Append(resource.MediaType == "image/png" ? "\\pngblip" : "\\jpegblip").Append("\\picwgoal").Append(Math.Max(1, Twips(inline.Width))).Append("\\pichgoal").Append(Math.Max(1, Twips(inline.Height))).Append(' ').Append(Convert.ToHexString(resource.Data.AsSpan())).Append('}');
                    }
                    else { ConversionDiagnostics.Report("rtf.inline-fallback", "Inline control or unavailable/unsupported image", "Exported the alternative text without fetching external resources.", inline.Id); b.Append(Escape(inline.AltText)); }
                }
                else b.Append(Escape(run.Text));
                b.Append('}'); if (run.Style.Hyperlink is not null) b.Append("}}");
            }
            b.Append("\\par\n");
        }
        void WriteBlocks(IEnumerable<Block> blocks, bool inTable = false, bool inSection = false)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph p: WriteParagraph(p, inTable); break;
                    case Section section:
                        if (section.Background is not null || section.BorderColor is not null || section.Borders is not null || section.PaddingEdges is not null || section.Padding != 12)
                            ConversionDiagnostics.Report("rtf.section-decoration", "Section background, borders or padding", "Retained section content without its decoration.", section.Id);
                        if (inTable || inSection) { ConversionDiagnostics.Report("rtf.nested-section", "Nested flow section", "Exported the section's blocks in its parent container.", section.Id); WriteBlocks(section.Blocks, inTable, inSection); }
                        else if (!document.Sections.IsEmpty || writingStory) WriteBlocks(section.Blocks, false, true);
                        else { b.Append("\\sectd\\sbknone "); WriteBlocks(section.Blocks, false, true); b.Append("\\sect\n"); }
                        break;
                    case Table table when inTable:
                        ConversionDiagnostics.Report("rtf.nested-table", "Nested table", "Flattened nested table cells to paragraphs inside the parent cell.", table.Id);
                        for (var r = 0; r < table.Rows.Length; r++) for (var c = 0; c < table.ColumnCount; c++) if (!table.IsCovered(r, c)) WriteBlocks(table.Rows[r][c].Blocks, true, inSection);
                        break;
                    case Table table:
                        Precision(table.Id, table.ColumnWidths.Concat(table.RowSizing.Select(s => s.Height)).ToArray());
                        for (var r = 0; r < table.Rows.Length; r++)
                        {
                            b.Append("\\trowd\\trgaph0\\trleft0");
                            var height = table.RowSizing.IsEmpty ? new TableRowSizing() : table.RowSizing[r];
                            b.Append("\\trrh").Append(height.Mode == TableRowHeightMode.Auto ? 0 : (height.Mode == TableRowHeightMode.Exact ? -1 : 1) * Math.Max(1, Twips(height.Height)));
                            var right = 0;
                            for (var c = 0; c < table.ColumnCount; c++)
                            {
                                var cell = table.Rows[r][c]; var owner = table.OwnerOf(r, c); var anchor = table.Rows[owner.Row][owner.Column];
                                if (cell.Padding is not null || cell.Borders is not null) ConversionDiagnostics.Report("rtf.cell-decoration", "Cell borders or padding", "Retained cell content and background without borders or padding.", cell.Id);
                                if (anchor.ColumnSpan > 1) b.Append(c == owner.Column ? "\\clmgf" : "\\clmrg");
                                if (anchor.RowSpan > 1) b.Append(r == owner.Row ? "\\clvmgf" : "\\clvmrg");
                                b.Append("\\clcbpat").Append(ColorIndex(cell.Background)); right += table.ColumnWidths.IsEmpty ? 1800 : Math.Max(1, Twips(table.ColumnWidths[c])); b.Append("\\cellx").Append(right);
                            }
                            b.Append(' ');
                            for (var c = 0; c < table.ColumnCount; c++) { if (!table.IsCovered(r, c)) WriteBlocks(table.Rows[r][c].Blocks, true, inSection); b.Append("\\cell "); }
                            b.Append("\\row\n");
                        }
                        b.Append("\\pard ");
                        break;
                }
        }
        if (!document.Sections.IsEmpty) WritePhysicalSection(document.Sections[0]);
        WriteBlocks(document.Blocks);
        foreach (var story in document.Stories.Values.Where(story => !exportedStories.Contains(story.Id)))
            ConversionDiagnostics.Report("rtf.unreferenced-story", "Unreferenced secondary story", "RTF retains stories attached to exported sections and note markers; omitted this unattached story.", story.Id);
        return b.Append('}').ToString();
    }
    private static void WriteLists(StringBuilder b, List<ExportList> lists)
    {
        if (lists.Count == 0) return;
        b.Append("{\\*\\listtable");
        foreach (var list in lists)
        {
            b.Append("{\\list\\listtemplateid").Append(list.Id);
            for (var index = 0; index < Math.Max(1, list.Definition.Levels.Length); index++)
            {
                var level = list.Definition.Level(index, ListKind.Numbered); var bullet = level.Kind == ListKind.Bullet || level.Marker == ListMarkerStyle.Bullet;
                var format = bullet ? 23 : level.Marker switch { ListMarkerStyle.UpperRoman => 1, ListMarkerStyle.LowerRoman => 2, ListMarkerStyle.UpperLetter => 3, ListMarkerStyle.LowerLetter => 4, _ => 0 };
                var pattern = bullet ? level.Text ?? "\u2022" : level.Prefix + (level.IncludeAncestors ? string.Join(".", Enumerable.Range(0, index + 1).Select(i => ((char)i).ToString())) : ((char)index).ToString()) + level.Suffix;
                b.Append("{\\listlevel\\levelnfc").Append(format).Append("\\leveljc0\\levelfollow0\\levelstartat").Append(level.Start).Append("{\\leveltext ");
                b.Append("\\'").Append(pattern.Length.ToString("x2", CultureInfo.InvariantCulture));
                foreach (var ch in pattern) if (ch <= 8) b.Append("\\'").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture)); else b.Append(Escape(ch.ToString()));
                b.Append(";}{\\levelnumbers ");
                for (var i = 0; i < pattern.Length; i++) if (pattern[i] <= 8) b.Append("\\'").Append((i + 1).ToString("x2", CultureInfo.InvariantCulture));
                b.Append(";}}");
            }
            b.Append("\\listid").Append(list.Id).Append('}');
        }
        b.Append("}{\\*\\listoverridetable");
        foreach (var list in lists)
        {
            b.Append("{\\listoverride\\listid").Append(list.Id).Append("\\listoverridecount9");
            for (var level = 0; level < 9; level++)
            {
                b.Append("{\\lfolevel");
                if (level == list.Level) b.Append("\\listoverridestartat\\levelstartat").Append(list.Start);
                else if (list.Ancestors.TryGetValue(level, out var ancestor)) b.Append("\\listoverridestartat\\levelstartat").Append(ancestor);
                b.Append('}');
            }
            b.Append("\\ls").Append(list.Id).Append('}');
        }
        b.Append('}');
    }
    private static IEnumerable<TableCell> AllCells(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            if (block is Section section) { foreach (var cell in AllCells(section.Blocks)) yield return cell; }
            else if (block is Table table) foreach (var row in table.Rows) foreach (var cell in row) { yield return cell; foreach (var child in AllCells(cell.Blocks)) yield return child; }
    }
    private static int Twips(double value) => (int)Math.Round(value * 15);
    private static int StretchPercent(int stretch) => stretch switch { 1 => 50, 2 => 63, 3 => 75, 4 => 88, 6 => 113, 7 => 125, 8 => 150, 9 => 200, _ => 100 };
    private static string Escape(string text)
    {
        var b = new StringBuilder();
        foreach (var ch in text)
            if (ch is '\\' or '{' or '}') b.Append('\\').Append(ch);
            else if (ch == '\t') b.Append("\\tab ");
            else if (ch is '\u2028' or '\n' or '\r') b.Append("\\line ");
            else if (ch > 127 || ch < 32) b.Append("\\u").Append(unchecked((short)ch)).Append('?');
            else b.Append(ch);
        return b.ToString();
    }
}
