using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Textalonia.Editing;

namespace Textalonia.Model.Fields;

public enum FieldUpdateMode { Ordinary, Page, All }
public sealed record FieldDiagnostic(Guid FieldId, string Code, string Message);
public sealed record FieldPageContext(int PageNumber, int PageCount, int SectionPageCount);
public sealed record FieldResolveContext(FlowDocument Document, DocumentField Field, FieldInstruction Instruction,
    string Argument, CultureInfo Culture)
{
    /// <summary>The recipient or child-row values scoped to this field.</summary>
    public IReadOnlyDictionary<string, object?> MergeValues { get; init; } = ImmutableDictionary<string, object?>.Empty;
}
/// <summary>A host-supplied image result for an INCLUDEPICTURE field. The evaluator never fetches the instruction URI.</summary>
public sealed record FieldPictureResult(DocumentResource Resource)
{
    public string AltText { get; init; } = "";
    public double Width { get; init; } = 32;
    public double Height { get; init; } = 32;
    public ImagePlacement? Placement { get; init; }
}
public sealed record FieldPaginationContext(string Signature, Func<DocumentField, FieldPageContext?> ResolvePage,
    Func<DocumentAnchor, int?>? ResolveAnchorPage = null);
public sealed record FieldEvaluationOptions
{
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
    /// <summary>The caller supplies the clock for reproducible results. The default is UnixEpoch.</summary>
    public DateTimeOffset Clock { get; init; } = DateTimeOffset.UnixEpoch;
    public IReadOnlyDictionary<string, object?> MergeValues { get; init; } = ImmutableDictionary<string, object?>.Empty;
    /// <summary>Supplies values scoped to the current field, including nested MERGEFIELD instructions.</summary>
    public Func<DocumentField, IReadOnlyDictionary<string, object?>>? MergeValuesResolver { get; init; }
    public Func<FieldResolveContext, FlowDocument?>? DocumentVariableResolver { get; init; }
    /// <summary>Supplies an image resource and display metadata without following an INCLUDEPICTURE path or URI.</summary>
    public Func<FieldResolveContext, FieldPictureResult?>? PictureResourceResolver { get; init; }
    public Func<FieldResolveContext, FlowDocument?>? PictureResolver { get; init; }
    public Func<DocumentField, FieldPageContext?>? PageContextResolver { get; init; }
    public Func<DocumentAnchor, int?>? AnchorPageResolver { get; init; }
    public FieldUpdateMode Mode { get; init; }
    public bool OnlyDirty { get; init; }
    public int MaximumDepth { get; init; } = 32;
    public int MaximumResultLength { get; init; } = 1_000_000;
    public CancellationToken CancellationToken { get; init; }
}
public sealed record FieldUpdateResult(FlowDocument Document, ImmutableArray<FieldDiagnostic> Diagnostics, int UpdatedCount);

/// <summary>Evaluates explicit general fields with bounded dependencies and host-only external resolution.</summary>
public static class FieldEvaluator
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MERGEFIELD", "IF", "DATE", "TIME", "CREATEDATE", "SAVEDATE", "PRINTDATE", "DOCPROPERTY",
        "AUTHOR", "TITLE", "SUBJECT", "KEYWORDS", "COMMENTS", "FILENAME", "NUMWORDS", "NUMCHARS", "HYPERLINK", "LASTSAVEDBY", "REVNUM", "TEMPLATE",
        "PAGE", "NUMPAGES", "SECTIONPAGES", "REF", "PAGEREF", "SEQ", "STYLEREF", "SYMBOL", "TC", "TOC",
        "DOCVARIABLE", "INCLUDEPICTURE", "="
    };
    public static bool IsSupported(string code) => Codes.Contains(code);
    public static FieldUpdateResult Update(FlowDocument document, FieldEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new();
        if (options.Culture is null || options.MergeValues is null || options.MaximumDepth is < 1 or > 128 ||
            options.MaximumResultLength is < 0 or > 16_000_000 || !Enum.IsDefined(options.Mode))
            throw new ArgumentException("Invalid field evaluation options.", nameof(options));
        document.Validate();
        return new Evaluation(document, options).Run();
    }
    /// <summary>Repaginates after changes, stopping only when both layout signature and cached results stabilize.</summary>
    public static FieldUpdateResult UpdateUntilStable(FlowDocument document, Func<FlowDocument, FieldPaginationContext> paginate,
        FieldEvaluationOptions? options = null, int maximumIterations = 8)
    {
        ArgumentNullException.ThrowIfNull(paginate);
        if (maximumIterations is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        options ??= new();
        var ordinary = Update(document, options with { Mode = FieldUpdateMode.Ordinary });
        document = ordinary.Document;
        var diagnostics = ordinary.Diagnostics.ToBuilder(); var updates = ordinary.UpdatedCount;
        string? previousSignature = null;
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var layout = paginate(document);
            // Ordinary fields were resolved before pagination. Replacing a page field may
            // mark other ranges dirty through anchor reconciliation, but those results
            // must retain their recipient-specific values during the page-only pass.
            var evaluated = Update(document, options with { Mode = FieldUpdateMode.Page,
                PageContextResolver = layout.ResolvePage, AnchorPageResolver = layout.ResolveAnchorPage });
            diagnostics.AddRange(evaluated.Diagnostics); updates += evaluated.UpdatedCount;
            if (evaluated.UpdatedCount == 0 && previousSignature == layout.Signature)
                return new(evaluated.Document, diagnostics.Distinct().ToImmutableArray(), updates);
            document = evaluated.Document; previousSignature = layout.Signature;
        }
        diagnostics.Add(new(Guid.Empty, "field.pagination-not-converged", $"Page-dependent fields did not stabilize in {maximumIterations} iterations."));
        return new(document, diagnostics.Distinct().ToImmutableArray(), updates);
    }

    private sealed class Evaluation(FlowDocument document, FieldEvaluationOptions options)
    {
        private FlowDocument _document = document;
        private readonly HashSet<Guid> _active = [];
        private readonly HashSet<Guid> _done = [];
        private readonly ImmutableArray<FieldDiagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<FieldDiagnostic>();
        private int _updates;
        internal FieldUpdateResult Run()
        {
            var order = _document.Fields.OrderBy(f => f.Start.StoryId).ThenBy(f => f.Start.Resolve(_document)).Select(f => f.Id).ToArray();
            foreach (var id in order)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                try { EvaluateField(id); }
                catch (FieldFailure error) { _diagnostics.Add(new(id, error.Code, error.Message)); }
                catch (FormatException error) { _diagnostics.Add(new(id, "field.invalid-instruction", error.Message)); }
                catch (ArgumentException error) { _diagnostics.Add(new(id, "field.invalid-result", error.Message)); }
                catch (ArithmeticException error) { _diagnostics.Add(new(id, "field.invalid-instruction", error.Message)); }
            }
            return new(_document, _diagnostics.ToImmutable(), _updates);
        }
        private void EvaluateField(Guid id)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var field = _document.Fields.FirstOrDefault(f => f.Id == id);
            if (field is null || _done.Contains(id) || field.IsLocked || options.OnlyDirty && !field.IsDirty) return;
            if (_active.Count >= options.MaximumDepth) throw new FieldFailure("field.depth-limit", "Field dependencies exceed the configured limit.");
            if (!_active.Add(id)) throw new FieldFailure("field.cycle", "Circular field/bookmark dependency; cached results were retained.");
            try
            {
                var instruction = FieldInstructionParser.Parse(field.Instruction, options.MaximumDepth);
                var page = IsPage(instruction);
                if (options.Mode == FieldUpdateMode.Ordinary && page || options.Mode == FieldUpdateMode.Page && !page) return;
                var result = Evaluate(instruction, field, 0);
                if (new DocumentIndex(result).Length > options.MaximumResultLength) throw new FieldFailure("field.result-limit", "Field result exceeds the configured limit.");
                result.Validate();
                field = _document.Fields.First(f => f.Id == id);
                var start = field.Start.Resolve(_document); var end = field.End.Resolve(_document);
                var current = _document.GetStoryIndex(field.Start.StoryId).ReadText(start, end - start);
                // Plain text equality avoids needless replacement and preserves existing character formatting.
                // Rich host and TOC results also compare style/content so formatting-only changes are applied.
                var rich = instruction.Code is "DOCVARIABLE" or "INCLUDEPICTURE" or "TOC" or "HYPERLINK" or "REF" or "PAGEREF" || result.Blocks.OfType<Paragraph>().Any(p => p.Runs.Any(r => r.Inline is not null));
                var unchanged = current == result.Text && (!rich || SameRichResult(field, result));
                if (!unchanged)
                {
                    _document = DocumentRangeEditing.Replace(_document, field.Start.StoryId, start, end - start, result);
                    if (instruction.Code == "INCLUDEPICTURE" && options.PictureResourceResolver is not null)
                        _document = _document.PruneUnusedResources();
                    _updates++;
                }
                var fields = _document.Fields.Select(f => f.Id == id ? f with { IsDirty = false } : f).ToImmutableArray();
                _document = _document with { Fields = fields }; _done.Add(id);
            }
            finally { _active.Remove(id); }
        }
        private bool SameRichResult(DocumentField field, FlowDocument result)
        {
            var index = _document.GetStoryIndex(field.Start.StoryId); var start = field.Start.Resolve(_document); var end = field.End.Resolve(_document);
            if (result.Blocks.Any(b => b is not Paragraph))
            {
                var projection = _document.GetStoryDocument(field.Start.StoryId) with { Fields = [], Bookmarks = [] };
                var current = DocumentFragments.Extract(projection, new DocumentIndex(projection), new(start, end)).Document;
                return StructuralKey(current.Blocks) == StructuralKey(result.Blocks) &&
                    result.Resources.All(pair => _document.Resources.TryGetValue(pair.Key, out var resource) && resource == pair.Value);
            }
            var slices = index.Paragraphs.Where(p => p.Start <= end && p.End >= start).Select(p =>
                (p.Paragraph.Style, CompareStyle: start == p.Start || new DocumentIndex(result).ParagraphCount > 1, Runs: p.Paragraph.Slice(Math.Max(0, start - p.Start), Math.Max(0, Math.Min(end, p.End) - Math.Max(start, p.Start))))).ToArray();
            var expected = new DocumentIndex(result).Paragraphs;
            return result.Resources.All(pair => _document.Resources.TryGetValue(pair.Key, out var resource) && resource == pair.Value) &&
                slices.Length == expected.Length && slices.Select((slice, i) =>
                    (!slice.CompareStyle || slice.Style with { TabStops = [] } == expected[i].Paragraph.Style with { TabStops = [] } &&
                    slice.Style.TabStops.SequenceEqual(expected[i].Paragraph.Style.TabStops)) && NormalizeRuns(slice.Runs).SequenceEqual(NormalizeRuns(expected[i].Paragraph.Runs))).All(v => v);
        }
        private static IEnumerable<RichRun> NormalizeRuns(IEnumerable<RichRun> runs) => runs.Select(run =>
            run.Inline is { } inline ? run with { Inline = inline with { Id = Guid.Empty } } : run);
        private static string PictureResourceId(Guid fieldId, DocumentResource resource)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"{(int)resource.Kind}\0{resource.MediaType}\0{resource.Location}\0"));
            hash.AppendData(resource.Data.AsSpan());
            return "field-picture-" + fieldId.ToString("N") + "-" + Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16));
        }
        private static string StructuralKey(ImmutableArray<Block> blocks)
        {
            Block Normalize(Block block) => block switch
            {
                Paragraph paragraph => paragraph with { Id = Guid.Empty, Runs = paragraph.Runs.Select(run =>
                    run.Inline is { } inline ? run with { Inline = inline with { Id = Guid.Empty } } : run).ToImmutableArray() },
                Section section => section with { Id = Guid.Empty, Blocks = section.Blocks.Select(Normalize).ToImmutableArray() },
                Table table => table with { Id = Guid.Empty, Rows = table.Rows.Select(row => row.Select(cell => cell with
                    { Id = Guid.Empty, Blocks = cell.Blocks.Select(Normalize).ToImmutableArray(), MergeOriginalBlocks = cell.MergeOriginalBlocks.Select(Normalize).ToImmutableArray() }).ToImmutableArray()).ToImmutableArray() },
                _ => block
            };
            return System.Text.Json.JsonSerializer.Serialize(blocks.Select(Normalize).ToImmutableArray());
        }
        private FlowDocument Evaluate(FieldInstruction instruction, DocumentField field, int depth)
        {
            if (depth >= options.MaximumDepth) throw new FieldFailure("field.depth-limit", "Field nesting exceeds the configured limit.");
            string Argument(int i) => i < instruction.Arguments.Length ? Value(instruction.Arguments[i], field, depth + 1) : "";
            string? Switch(string name) => instruction.Switch(name)?.Argument is { } argument ? Value(argument, field, depth + 1) : null;
            IReadOnlyDictionary<string, object?> ValuesForField() => options.MergeValuesResolver?.Invoke(field) ?? options.MergeValues;
            FieldResolveContext ResolveContext() => new(_document, field, instruction, Argument(0), options.Culture)
            { MergeValues = ValuesForField() };
            ValidateSwitches(instruction);
            object? value;
            switch (instruction.Code)
            {
                case "MERGEFIELD":
                    var mergeValues = ValuesForField();
                    if (!mergeValues.TryGetValue(Argument(0), out value) || value is null) value = field.LegacyMergeField?.FallbackText ?? "";
                    if (field.LegacyMergeField?.Format is { } legacyFormat && value is IFormattable formattable) value = formattable.ToString(legacyFormat, options.Culture);
                    break;
                case "IF":
                    if (instruction.Arguments.Length != 5) throw new FormatException("IF requires two operands, a comparison, and two results.");
                    var left = Argument(0); var right = Argument(2);
                    var comparison = double.TryParse(left, NumberStyles.Number, options.Culture, out var a) &&
                        double.TryParse(right, NumberStyles.Number, options.Culture, out var b) ? a.CompareTo(b) : options.Culture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase);
                    var condition = Argument(1) switch { "=" or "==" => comparison == 0, "<>" or "!=" => comparison != 0,
                        "<" => comparison < 0, ">" => comparison > 0, "<=" => comparison <= 0, ">=" => comparison >= 0, _ => throw new FormatException("Unsupported IF comparison.") };
                    value = Argument(condition ? 3 : 4); break;
                case "DATE": case "TIME": value = options.Clock; break;
                case "CREATEDATE": case "SAVEDATE": case "PRINTDATE":
                    value = DateTimeOffset.TryParse(Property(instruction.Code), options.Culture, DateTimeStyles.None, out var date) ? date :
                        throw new FieldFailure("field.missing-property", $"Document property {instruction.Code} is unavailable."); break;
                case "DOCPROPERTY": value = Property(Argument(0)); break;
                case "AUTHOR": case "TITLE": case "SUBJECT": case "KEYWORDS": case "COMMENTS": case "FILENAME": case "LASTSAVEDBY": case "REVNUM": case "TEMPLATE": value = Property(instruction.Code); break;
                case "NUMWORDS": value = _document.PlainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length; break;
                case "NUMCHARS": value = _document.Text.Length; break;
                case "PAGE": case "NUMPAGES": case "SECTIONPAGES":
                    var page = options.PageContextResolver?.Invoke(field) ?? throw new FieldFailure("field.page-context-required", "Page field requires a layout context.");
                    value = instruction.Code switch { "PAGE" => page.PageNumber, "NUMPAGES" => page.PageCount, _ => page.SectionPageCount }; break;
                case "REF":
                    if (instruction.Switches.All(s => s.Name.Equals("h", StringComparison.OrdinalIgnoreCase)))
                    {
                        var reference = RichReference(Argument(0));
                        return instruction.Switch("h") is null ? reference : reference.RewriteParagraphs(p =>
                            [p with { Runs = p.Runs.Select(r => r with { Style = r.Style with { InternalLink = new() { BookmarkName = Argument(0) } } }).ToImmutableArray() }]);
                    }
                    value = Reference(Argument(0)); break;
                case "PAGEREF":
                    var bookmark = Bookmark(Argument(0));
                    value = options.AnchorPageResolver?.Invoke(bookmark.Start) ?? throw new FieldFailure("field.page-context-required", "PAGEREF requires an anchor page resolver."); break;
                case "SEQ": value = Sequence(instruction, field, Argument(0)); if (instruction.Switch("h") is not null) value = ""; break;
                case "STYLEREF": value = StyleReference(field, Argument(0), instruction.Switch("l") is not null); break;
                case "SYMBOL":
                    var symbol = Argument(0); var code = symbol.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(symbol[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(symbol, CultureInfo.InvariantCulture);
                    if (!System.Text.Rune.IsValid(code)) throw new FormatException("SYMBOL requires a valid Unicode scalar value.");
                    value = char.ConvertFromUtf32(code); break;
                case "HYPERLINK":
                    var source = _document.GetStoryIndex(field.Start.StoryId); var from = field.Start.Resolve(_document); var to = field.End.Resolve(_document);
                    var label = source.ReadPlainText(from, to - from);
                    var hyperlinkStyle = source.At(from).Paragraph.StyleAt(field.Start.Offset);
                    if (Switch("l") is { } destination)
                        hyperlinkStyle = hyperlinkStyle with { InternalLink = new() { BookmarkName = destination, Tooltip = Switch("o") } };
                    else
                    {
                        if (Switch("o") is not null) throw new FieldFailure("field.unsupported-switch", "External hyperlink tooltips are not represented by the URI-only external link model.");
                        if (!FlowDocument.IsSafeHyperlink(Argument(0))) throw new FieldFailure("field.unsafe-hyperlink", "HYPERLINK uses an unsupported URI scheme.");
                        hyperlinkStyle = hyperlinkStyle with { Hyperlink = Argument(0) };
                    }
                    return FlowDocument.FromText(label.Length == 0 ? Argument(0) : label, hyperlinkStyle);
                case "TC": value = ""; break;
                case "TOC": return Contents(instruction, field, depth);
                case "DOCVARIABLE":
                    return options.DocumentVariableResolver?.Invoke(ResolveContext()) ??
                        throw new FieldFailure("field.host-resolver-required", "DOCVARIABLE requires an explicit host result resolver.");
                case "INCLUDEPICTURE":
                    var pictureContext = ResolveContext();
                    if (options.PictureResourceResolver is { } pictureResolver)
                    {
                        var picture = pictureResolver(pictureContext) ?? throw new FieldFailure("field.host-resolver-required", "INCLUDEPICTURE requires an explicit host result resolver.");
                        if (picture.Resource is null) throw new FieldFailure("field.invalid-result", "INCLUDEPICTURE returned no image resource.");
                        var resourceId = PictureResourceId(field.Id, picture.Resource);
                        var image = new InlineDescriptor { Payload = new ImageInlinePayload(resourceId), AltText = picture.AltText,
                            Width = picture.Width, Height = picture.Height, Placement = picture.Placement };
                        var pictureStyle = _document.GetStoryIndex(field.Start.StoryId).At(field.Start.Resolve(_document)).Paragraph.StyleAt(field.Start.Offset);
                        return new FlowDocument([new Paragraph([new RichRun(image, pictureStyle)])])
                        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add(resourceId, picture.Resource) };
                    }
                    return options.PictureResolver?.Invoke(pictureContext) ??
                        throw new FieldFailure("field.host-resolver-required", "INCLUDEPICTURE requires an explicit host result resolver.");
                case "=": value = new FieldFormula(string.Join(" ", instruction.Arguments.Select(a => Value(a, field, depth + 1))),
                    name => double.TryParse(Reference(name), NumberStyles.Number, options.Culture, out var number) ? number : throw new FormatException($"Bookmark {name} is not numeric."), options.Culture).Evaluate(); break;
                default: throw new FieldFailure("field.unsupported-code", $"Unsupported field code {instruction.Code}; its instruction and cached result were retained.");
            }
            var text = Format(value, instruction, Switch("#"), Switch("@"));
            if (instruction.Code == "MERGEFIELD" && text.Length > 0) text = (Switch("b") ?? "") + text + (Switch("f") ?? "");
            foreach (var general in instruction.Switches.Where(s => s.Name == "*"))
            {
                var name = general.Argument is { } arg ? Value(arg, field, depth + 1) : "";
                text = name.ToUpperInvariant() switch
                {
                    "UPPER" => options.Culture.TextInfo.ToUpper(text), "LOWER" => options.Culture.TextInfo.ToLower(text),
                    "CAPS" => options.Culture.TextInfo.ToTitleCase(text), "FIRSTCAP" when text.Length > 0 => options.Culture.TextInfo.ToUpper(text[..1]) + text[1..],
                    "ROMAN" or "ALPHABETIC" when int.TryParse(text, NumberStyles.Integer, options.Culture, out var number) => DocumentNoteNumbering.Format(number,
                        name == "roman" ? PageNumberFormat.LowerRoman : name == "alphabetic" ? PageNumberFormat.LowerLetter : name.ToUpperInvariant() == "ROMAN" ? PageNumberFormat.UpperRoman : PageNumberFormat.UpperLetter),
                    "ARABIC" or "MERGEFORMAT" or "CHARFORMAT" => text,
                    _ => throw new FieldFailure("field.unsupported-switch", $"Unsupported general switch {name}; cached result was retained.")
                };
            }
            var style = _document.GetStoryIndex(field.Start.StoryId).At(field.Start.Resolve(_document)).Paragraph.StyleAt(field.Start.Offset);
            if (instruction.Code is "PAGE" or "NUMPAGES" or "SECTIONPAGES" &&
                _document.Stories.TryGetValue(field.Start.StoryId, out var repeatedStory) && repeatedStory.Kind is DocumentStoryKind.Header or DocumentStoryKind.Footer)
            {
                if (depth == 0 && instruction.Switches.All(s => s.Name == "*" && s.Argument?.Literal?.ToUpperInvariant() is "MERGEFORMAT" or "CHARFORMAT"))
                {
                    var kind = instruction.Code switch { "PAGE" => PageFieldKind.Page, "NUMPAGES" => PageFieldKind.NumPages, _ => PageFieldKind.SectionPages };
                    return new FlowDocument([new Paragraph([new RichRun(InlineDescriptor.PageField(kind) with { AltText = text }, style)])
                        { Style = _document.GetStoryIndex(field.Start.StoryId).At(field.Start.Resolve(_document)).Paragraph.Style }]);
                }
                _diagnostics.Add(new(field.Id, "field.repeated-page-format", "Nested or formatted header/footer page fields retain one cached context; per-page instances support simple unformatted page fields."));
            }
            if (instruction.Code == "PAGEREF" && instruction.Switch("h") is not null)
                style = style with { InternalLink = new() { BookmarkName = Argument(0) } };
            if (instruction.Code == "SYMBOL")
            {
                if (Switch("f") is { } font) style = style with { FontFamily = font };
                if (Switch("s") is { } size) style = style with { FontSize = double.Parse(size, CultureInfo.InvariantCulture) };
            }
            return FlowDocument.FromText(text, style);
        }
        private static void ValidateSwitches(FieldInstruction instruction)
        {
            var specific = instruction.Code switch
            {
                "MERGEFIELD" => "bf", "SEQ" => "rcnhs", "STYLEREF" => "l", "SYMBOL" => "fsu",
                "TOC" => "houftlcnp", "TC" => "fln", "REF" => "h", "PAGEREF" => "h", "HYPERLINK" => "lo",
                "INCLUDEPICTURE" => "d", _ => ""
            };
            foreach (var fieldSwitch in instruction.Switches)
                if (fieldSwitch.Name is not ("*" or "#" or "@") &&
                    (fieldSwitch.Name.Length != 1 || !specific.Contains(fieldSwitch.Name.ToLowerInvariant(), StringComparison.Ordinal)))
                    throw new FieldFailure("field.unsupported-switch", $"Unsupported {instruction.Code} switch {fieldSwitch.Name}; cached result was retained.");
        }
        private string Format(object? value, FieldInstruction instruction, string? numeric, string? date)
        {
            if (date is not null)
            {
                if (value is not DateTimeOffset && value is not DateTime && DateTimeOffset.TryParse(Convert.ToString(value, options.Culture), options.Culture, DateTimeStyles.None, out var parsed)) value = parsed;
                // Word's AM/PM token maps to .NET's explicit designator.
                date = date.Replace("AM/PM", "tt", StringComparison.OrdinalIgnoreCase);
                return value switch
                {
                    DateTimeOffset dateOffset => dateOffset.ToString(date, options.Culture),
                    DateTime dateTime => dateTime.ToString(date, options.Culture),
                    _ => throw new FormatException("Date switch requires a date value.")
                };
            }
            if (numeric is not null)
            {
                if (value is string text && decimal.TryParse(text, NumberStyles.Number, options.Culture, out var number)) value = number;
                return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal ?
                    ((IFormattable)value).ToString(numeric, options.Culture) : throw new FormatException("Numeric switch requires a number.");
            }
            if (value is DateTimeOffset timestamp) return timestamp.ToString(instruction.Code == "TIME" ? "T" : "d", options.Culture);
            return Convert.ToString(value, options.Culture) ?? "";
        }
        private string Value(FieldArgument argument, DocumentField field, int depth) => string.Concat(argument.Parts.Select(part => part switch
        { FieldLiteral literal => literal.Value, FieldNested nested => Evaluate(nested.Instruction, field, depth).PlainText, _ => "" }));
        private string Property(string name)
        {
            var legacy = _document.Properties.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (legacy is not null) return legacy;
            var custom = _document.CustomProperties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (custom is not null) return custom.Value;
            var core = _document.CoreProperties;
            var value = name.ToUpperInvariant() switch
            {
                "TITLE" => core.Title, "SUBJECT" => core.Subject, "AUTHOR" or "CREATOR" => core.Creator,
                "KEYWORDS" => core.Keywords, "COMMENTS" or "DESCRIPTION" => core.Description,
                "LASTSAVEDBY" or "LASTMODIFIEDBY" => core.LastModifiedBy, "REVNUM" or "REVISION" => core.Revision,
                "CATEGORY" => core.Category, "CONTENTSTATUS" => core.ContentStatus,
                "IDENTIFIER" => core.Identifier, "LANGUAGE" => core.Language, "VERSION" => core.Version,
                "CREATEDATE" => core.Created?.ToString("O", CultureInfo.InvariantCulture),
                "SAVEDATE" => core.Modified?.ToString("O", CultureInfo.InvariantCulture),
                _ => null
            };
            return value ?? throw new FieldFailure("field.missing-property", $"Document property {name} is unavailable.");
        }
        private DocumentBookmark Bookmark(string name) => _document.Bookmarks.FirstOrDefault(b => b.Name.Equals(name, StringComparison.Ordinal)) ??
            throw new FieldFailure("field.missing-bookmark", $"Bookmark {name} does not exist.");
        private string Reference(string name)
        {
            var bookmark = Bookmark(name); var start = bookmark.Start.Resolve(_document); var end = bookmark.End.Resolve(_document);
            foreach (var dependency in _document.Fields.Where(f => f.Start.StoryId == bookmark.Start.StoryId && f.Start.Resolve(_document) >= start && f.End.Resolve(_document) <= end).Select(f => f.Id).ToArray()) EvaluateField(dependency);
            bookmark = Bookmark(name); start = bookmark.Start.Resolve(_document); end = bookmark.End.Resolve(_document);
            return _document.GetStoryIndex(bookmark.Start.StoryId).ReadPlainText(start, end - start);
        }
        private FlowDocument RichReference(string name)
        {
            _ = Reference(name); // Resolve dependencies first, then take the updated rich span.
            var bookmark = Bookmark(name); var start = bookmark.Start.Resolve(_document); var end = bookmark.End.Resolve(_document);
            var projection = _document.GetStoryDocument(bookmark.Start.StoryId) with { Fields = [], Bookmarks = [] };
            var fragment = DocumentFragments.Extract(projection, new DocumentIndex(projection), new(start, end)).Document;
            return fragment with { Fields = [], Bookmarks = [], Sections = [] };
        }
        private int Sequence(FieldInstruction instruction, DocumentField field, string name)
        {
            var count = 0; var position = field.Start.Resolve(_document); var after = -1;
            if (instruction.Switch("s")?.Argument?.Literal is { } heading)
            {
                var level = int.Parse(heading, CultureInfo.InvariantCulture);
                if (level is < 1 or > 9) throw new FormatException("Invalid SEQ heading restart level.");
                var styleResolver = new DocumentStyleResolver(_document);
                after = _document.GetStoryIndex(field.Start.StoryId).Paragraphs.LastOrDefault(p => p.Start <= position &&
                    styleResolver.ResolveParagraphStyle(p.Paragraph.Style).HeadingLevel is > 0 and var headingLevel && headingLevel <= level)?.Start ?? -1;
            }
            foreach (var previous in _document.Fields.Where(f => f.Start.StoryId == field.Start.StoryId && f.Start.Resolve(_document) <= position && f.Start.Resolve(_document) >= after)
                .OrderBy(f => f.Start.Resolve(_document)))
            {
                FieldInstruction parsed;
                try { parsed = previous.Id == field.Id ? instruction : FieldInstructionParser.Parse(previous.Instruction, options.MaximumDepth); }
                catch (FormatException) { continue; }
                if (parsed.Code != "SEQ" || parsed.Arguments.FirstOrDefault()?.Literal != name) continue;
                if (parsed.Switch("r")?.Argument?.Literal is { } restart) count = int.Parse(restart, CultureInfo.InvariantCulture);
                else if (parsed.Switch("c") is null) count++;
                if (previous.Id == field.Id) break;
            }
            return count;
        }
        private string StyleReference(DocumentField field, string name, bool last)
        {
            var index = _document.GetStoryIndex(field.Start.StoryId); var position = field.Start.Resolve(_document);
            var resolver = new DocumentStyleResolver(_document);
            var paragraphs = index.Paragraphs.Where(p => p.Paragraph.Style.StyleId?.Equals(name, StringComparison.Ordinal) == true ||
                p.Paragraph.Style.StyleId is { } id && _document.Styles.Paragraphs.TryGetValue(id, out var definition) && definition.Name?.Equals(name, StringComparison.Ordinal) == true ||
                int.TryParse(name, out var level) && resolver.ResolveParagraphStyle(p.Paragraph.Style).HeadingLevel == level).ToArray();
            var candidate = last ? paragraphs.LastOrDefault() : paragraphs.LastOrDefault(p => p.Start <= position) ?? paragraphs.FirstOrDefault();
            return candidate?.Paragraph.PlainText ?? throw new FieldFailure("field.missing-style", $"No paragraph matches style {name}.");
        }
        private FlowDocument Contents(FieldInstruction instruction, DocumentField field, int depth)
        {
            string? Switch(string name) => instruction.Switch(name)?.Argument is { } arg ? Value(arg, field, depth + 1) : null;
            var minimum = 1; var maximum = 3;
            if ((Switch("o") ?? Switch("l")) is { } outline)
            {
                var levels = outline.Split('-');
                if (levels.Length != 2 || !int.TryParse(levels[0], out minimum) || !int.TryParse(levels[1], out maximum) || minimum < 1 || maximum > 9 || minimum > maximum)
                    throw new FormatException("TOC outline range must be between 1 and 9.");
            }
            var customStyles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (Switch("t") is { } styleList)
            {
                var values = styleList.Split(',');
                if (values.Length % 2 != 0) throw new FormatException("TOC style switch requires style,level pairs.");
                for (var i = 0; i < values.Length; i += 2)
                {
                    if (!int.TryParse(values[i + 1], out var level) || level is < 1 or > 9) throw new FormatException("Invalid TOC style level.");
                    customStyles[values[i].Trim()] = level;
                }
            }
            if (Switch("t") is not null && Switch("o") is null && Switch("l") is null) maximum = 9;
            // Caption text contains SEQ cached results; update those dependencies before reading entries.
            if (Switch("c") is { } sequenceName)
                foreach (var sequenceId in _document.Fields.Where(f => f.Start.StoryId == Guid.Empty && f.Id != field.Id).Where(f =>
                {
                    try { var code = FieldInstructionParser.Parse(f.Instruction, options.MaximumDepth); return code.Code == "SEQ" && code.Arguments.FirstOrDefault()?.Literal == sequenceName; }
                    catch (FormatException) { return false; }
                }).Select(f => f.Id).ToArray()) EvaluateField(sequenceId);
            field = _document.Fields.First(f => f.Id == field.Id);
            var index = new DocumentIndex(_document); var resolver = new DocumentStyleResolver(_document);
            var entries = new List<(int Position, int End, int Level, string Text, string Name, bool SuppressPage)>();
            var ownStart = field.Start.StoryId == Guid.Empty ? field.Start.Resolve(_document) : -1;
            var ownEnd = field.Start.StoryId == Guid.Empty ? field.End.Resolve(_document) : -1;
            var caption = Switch("c"); var tcOnly = instruction.Switch("f") is not null;
            if (caption is null && !tcOnly)
                foreach (var paragraph in index.Paragraphs)
                {
                    if (paragraph.Start >= ownStart && paragraph.End <= ownEnd) continue;
                    var style = resolver.ResolveParagraphStyle(paragraph.Paragraph.Style);
                    var styleId = paragraph.Paragraph.Style.StyleId;
                    var styleName = styleId is not null && _document.Styles.Paragraphs.TryGetValue(styleId, out var definition) ? definition.Name : null;
                    var level = styleId is not null && customStyles.TryGetValue(styleId, out var customLevel) ? customLevel :
                        styleName is not null && customStyles.TryGetValue(styleName, out customLevel) ? customLevel :
                        instruction.Switch("u") is not null && style.OutlineLevel > 0 ? style.OutlineLevel :
                        customStyles.Count == 0 || instruction.Switch("o") is not null ? style.HeadingLevel : 0;
                    if (level < minimum || level > maximum) continue;
                    entries.Add((paragraph.Start, paragraph.End, level, paragraph.Paragraph.PlainText, "_Toc" + paragraph.Paragraph.Id.ToString("N"), false));
                }
            foreach (var entry in _document.Fields.Where(f => f.Start.StoryId == Guid.Empty && f.Id != field.Id))
            {
                FieldInstruction code;
                try { code = FieldInstructionParser.Parse(entry.Instruction, options.MaximumDepth); }
                catch (FormatException) { continue; }
                var position = entry.Start.Resolve(_document);
                if (position >= ownStart && position < ownEnd) continue;
                if (caption is not null && code.Code == "SEQ" && code.Arguments.FirstOrDefault()?.Literal == caption)
                {
                    var paragraph = index.At(position);
                    entries.Add((paragraph.Start, paragraph.End, 1, paragraph.Paragraph.PlainText, "_Toc" + paragraph.Paragraph.Id.ToString("N"), false));
                }
                else if (caption is null && code.Code == "TC" && (Switch("f") is not { } identifier || code.Switch("f")?.Argument?.Literal == identifier))
                {
                    var level = code.Switch("l")?.Argument?.Literal is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : 1;
                    if (level < minimum || level > maximum) continue;
                    entries.Add((position, entry.End.Resolve(_document), level,
                        code.Arguments.Length > 0 ? Value(code.Arguments[0], entry, depth + 1) : "", "_Toc" + entry.Id.ToString("N"), code.Switch("n") is not null));
                }
            }
            var blocks = new List<Block>();
            foreach (var entry in entries.OrderBy(e => e.Position).DistinctBy(e => e.Name))
            {
                var bookmark = _document.Bookmarks.FirstOrDefault(b => b.Name == entry.Name);
                if (bookmark is null)
                {
                    bookmark = new DocumentBookmark { Name = entry.Name,
                        Start = DocumentAnchor.Create(_document, Guid.Empty, entry.Position, AnchorAffinity.Before),
                        End = DocumentAnchor.Create(_document, Guid.Empty, entry.End, AnchorAffinity.After) };
                    _document = _document with { Bookmarks = _document.Bookmarks.Add(bookmark) };
                }
                var style = TextStyle.Default;
                if (instruction.Switch("h") is not null) style = style with { InternalLink = new() { BookmarkName = bookmark.Name } };
                var runs = new List<RichRun> { new(entry.Text, style) };
                if (instruction.Switch("n") is null && !entry.SuppressPage)
                {
                    var page = options.AnchorPageResolver?.Invoke(bookmark.Start);
                    runs.Add(new((Switch("p") ?? "\t") + (page?.ToString(options.Culture) ?? "?"), style));
                }
                blocks.Add(new Paragraph(runs) { Style = ParagraphStyle.Default with
                { Indent = (entry.Level - 1) * 18, TabStops = [new(432, TabAlignment.Right, TabLeader.Dots)] } });
            }
            return new FlowDocument(blocks);
        }
        private static bool IsPage(FieldInstruction instruction) => instruction.Code is "PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF" ||
            instruction.Code == "TOC" && instruction.Switch("n") is null ||
            instruction.Arguments.Concat(instruction.Switches.Where(s => s.Argument is not null).Select(s => s.Argument!)).Any(a => a.Parts.OfType<FieldNested>().Any(n => IsPage(n.Instruction)));
        private sealed class FieldFailure(string code, string message) : Exception(message)
        { internal string Code { get; } = code; }
    }
}
