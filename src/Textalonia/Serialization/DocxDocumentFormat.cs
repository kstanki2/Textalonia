using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Bounded WordprocessingML interchange; page layout and revision history are diagnosed flow-model losses.</summary>
public sealed partial class DocxDocumentFormat : IDocumentFormat
{
    private const string DocumentMainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string TemplateMainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
    private readonly bool _template;

    public DocxDocumentFormat() { }
    internal DocxDocumentFormat(bool template) => _template = template;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var value = (uint)index;
        for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
        return value;
    }).ToArray();
    private const string ListNamePrefix = "Textalonia.List.";
    private const string SectionTag = "Textalonia.Section";
    private const string CellEndStyle = "TextaloniaCellEnd";
    public string Name => _template ? "Word template" : "Word document";
    public IReadOnlyList<string> Extensions => _template ? [".dotx"] : [".docx"];
    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() => Read(bytes, cancellationToken, _template), cancellationToken);
    }
    /// <summary>Loads a DOCX or DOTX protected with Standard Office AES password encryption.</summary>
    public async Task<FlowDocument> LoadWithPasswordAsync(Stream stream, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() => Read(OfficeEncryptedPackage.Decrypt(bytes, password, cancellationToken),
            cancellationToken, _template), cancellationToken);
    }
    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        document.Validate();
        var bytes = await Task.Run(() => Write(document, cancellationToken, _template), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
    private sealed record NumberingInfo(Guid Identity, ListDefinition Definition, Dictionary<int, int> Starts);
    private sealed class ImportedField(XElement source, TextStyle style, string? instruction = null)
    {
        internal XElement Source { get; } = source;
        internal StringBuilder Instruction { get; } = new(instruction ?? "");
        internal List<RichRun> Results { get; } = [];
        internal TextStyle Style { get; set; } = style;
        internal TextStyle? ResultStyle { get; set; }
        internal bool HasInstructionStyle { get; set; }
        internal bool Simple { get; } = instruction is not null;
        internal bool Separated { get; set; } = instruction is not null;
        internal bool Invalid { get; set; }
    }
    private static void Loss(string code, string feature, string fallback, XElement? source = null, Guid? id = null) =>
        ConversionDiagnostics.Report("docx." + code, feature, fallback, id,
            source is IXmlLineInfo info && info.HasLineInfo() ? $"{source.Document?.Annotation<string>() ?? "word/document.xml"}:{info.LineNumber}:{info.LinePosition}" : null);

    private static FlowDocument Read(byte[] bytes, CancellationToken token, bool template)
    {
        if (OfficeEncryptedPackage.IsEncrypted(bytes))
            throw new NotSupportedException("Password-encrypted Office package. Use LoadWithPasswordAsync for supported Standard AES packages.");
        if (OfficeEncryptedPackage.IsCompoundFile(bytes))
            throw new FormatException("DOCX input is a compound file, not an Open XML ZIP package.");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
            throw new FormatException("DOCX package is too large.");
        if (archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new FormatException("DOCX package contains duplicate parts.");
        long packageReadBytes = 0;
        var verifiedParts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] ReadPart(ZipArchiveEntry entry, int limit)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length > limit) throw new FormatException("DOCX package part exceeds its size limit.");
            if (verifiedParts.TryGetValue(entry.FullName, out var verified)) return verified;
            using var input = entry.Open(); using var output = new MemoryStream();
            var buffer = new byte[81920];
            var crc = uint.MaxValue;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + read > limit || packageReadBytes + read > 128L * 1024 * 1024)
                    throw new FormatException("DOCX decompressed package content exceeds its size limit.");
                for (var i = 0; i < read; i++) crc = CrcTable[(int)((crc ^ buffer[i]) & 255)] ^ (crc >> 8);
                packageReadBytes += read;
                output.Write(buffer, 0, read);
            }
            // ZipArchive may truncate deflate output to a forged declared size; validate integrity as well as budgets.
            if (output.Length != entry.Length || ~crc != entry.Crc32) throw new FormatException("DOCX package part has an inconsistent size or checksum.");
            verified = output.ToArray();
            verifiedParts.Add(entry.FullName, verified);
            return verified;
        }
        XDocument? Xml(string path, bool required = false)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(path);
            if (entry is null)
            {
                if (required) throw new FormatException($"Missing DOCX part: {path}");
                return null;
            }
            if (entry.Length > 32 * 1024 * 1024) throw new FormatException("DOCX XML part is too large.");
            using var source = new MemoryStream(ReadPart(entry, 32 * 1024 * 1024));
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            var xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
            xml.AddAnnotation(path);
            if (xml.Descendants().Any(e => e.Ancestors().Take(129).Count() > 128)) throw new FormatException("DOCX XML nesting exceeds the limit.");
            PreparePermissionMarkers(xml);
            PrepareLegacyForms(xml);
            PrepareGeneralFields(xml);
            return xml;
        }
        ValidateImportPackage(archive, path => Xml(path), token);
        var contentTypes = Xml("[Content_Types].xml", required: template)?.Root;
        if (template && (string?)contentTypes?.Elements(Ct + "Override")
                .FirstOrDefault(e => (string?)e.Attribute("PartName") == "/word/document.xml")
                ?.Attribute("ContentType") != TemplateMainContentType)
            throw new FormatException("DOTX package must declare a Word template main document part.");
        var relationships = Xml("word/_rels/document.xml.rels")?.Root?.Elements(Rel + "Relationship")
            .Where(e => e.Attribute("Id") is not null).ToDictionary(e => (string)e.Attribute("Id")!, e => e) ?? [];
        var mainRelationships = relationships;
        var currentPart = "word/document.xml";
        var settings = Xml("word/settings.xml");
        var glossaryRelationship = relationships.Values.FirstOrDefault(e => (string?)e.Attribute("Type") == R.NamespaceName + "/glossaryDocument" && (string?)e.Attribute("TargetMode") != "External");
        var glossaryPart = glossaryRelationship is null ? null : ResolvePart((string?)glossaryRelationship.Attribute("Target") ?? "");
        var placeholderTexts = (glossaryPart is null ? null : Xml(glossaryPart))?.Descendants(W + "docPart").Where(e => Value(e.Element(W + "docPartPr")?.Element(W + "name")) is not null)
            .GroupBy(e => Value(e.Element(W + "docPartPr")?.Element(W + "name"))!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => string.Concat(g.First().Element(W + "docPartBody")?.Descendants(W + "t").Select(e => e.Value) ?? []), StringComparer.Ordinal) ?? [];
        if (placeholderTexts.Count > 10000 || placeholderTexts.Values.Any(value => value.Length > 16384)) throw new FormatException("DOCX placeholder glossary exceeds its limits.");
        var stories = ImmutableDictionary.CreateBuilder<Guid, DocumentStory>();
        var notes = new Dictionary<(bool Endnote, string Id), DocumentNote>();
        var referencedNotes = new HashSet<Guid>();
        var notePrefixText = new Dictionary<Guid, string>();
        var paragraphSources = new Dictionary<XElement, Guid>();
        var bookmarks = new List<DocumentBookmark>();
        var generalFields = new List<DocumentField>();
        var contentControls = new List<DocumentContentControl>();
        var permissionRanges = new List<DocumentPermissionRange>();
        var permissionStarts = new Dictionary<(string, string), DocumentPermissionRange>();
        var controlStarts = new Dictionary<(string, string), DocumentContentControl>();
        var bookmarkStarts = new Dictionary<(string, string), DocumentBookmark>();
        var bookmarkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var footnoteSettings = ReadNoteSettings(settings?.Root?.Element(W + "footnotePr"), false);
        var endnoteSettings = ReadNoteSettings(settings?.Root?.Element(W + "endnotePr"), true);
        foreach (var mailMerge in settings?.Descendants(W + "mailMerge") ?? [])
            Loss("mail-merge-source", "Linked mail-merge recipient source and settings",
                "Retained document merge fields without the recipient connection or merge configuration; no source was accessed.", mailMerge);
        var styleRoot = Xml("word/styles.xml")?.Root;
        var styles = styleRoot?.Elements(W + "style").Where(e => e.Attribute(W + "styleId") is not null)
            .ToDictionary(e => (string)e.Attribute(W + "styleId")!, e => e) ?? [];
        var numbering = ReadNumbering(Xml("word/numbering.xml")?.Root);
        var defaults = styleRoot?.Element(W + "docDefaults");
        var defaultText = ReadTextStyle(defaults?.Element(W + "rPrDefault")?.Element(W + "rPr"), TextStyle.Default);
        var defaultParagraph = ReadParagraphStyle(defaults?.Element(W + "pPrDefault")?.Element(W + "pPr"), ParagraphStyle.Default with { DefaultTabWidth = 48 }, numbering);
        if (settings?.Root?.Element(W + "defaultTabStop") is { } defaultTabs) defaultParagraph = defaultParagraph with { DefaultTabWidth = Bounded(Number(defaultTabs) / 15d, 1, 100000, defaultTabs) };
        var defaultParagraphId = styles.Values.FirstOrDefault(e => (string?)e.Attribute(W + "type") == "paragraph" && OnAttribute(e, "default"))?.Attribute(W + "styleId")?.Value;
        var catalog = ReadStyleCatalog(styles, numbering);
        var themeRelationship = relationships.Values.FirstOrDefault(e => (string?)e.Attribute("Type") == R.NamespaceName + "/theme" && (string?)e.Attribute("TargetMode") != "External");
        var themePart = themeRelationship is null ? null : ResolvePart((string?)themeRelationship.Attribute("Target") ?? "");
        var theme = ReadTheme(themePart is null ? null : Xml(themePart)?.Root);
        var resources = ImmutableDictionary.CreateBuilder<string, DocumentResource>();
        var fonts = ReadEmbeddedFonts(Xml("word/fontTable.xml")?.Root, Xml("word/_rels/fontTable.xml.rels"),
            (path, limit) => (byte[])ReadPart(archive.GetEntry(path) ?? throw new FormatException("Missing DOCX embedded font part."), limit).Clone(), resources);
        var imageResources = new Dictionary<string, string>();
        var startsUsed = new HashSet<(string, int)>();
        var resourceBytes = resources.Values.Sum(resource => (long)resource.Data.Length);
        (TextStyle Text, ParagraphStyle Paragraph) ResolveStyle(string? id, HashSet<string>? visited = null)
        {
            if (id is null) return (defaultText, defaultParagraph);
            if (!styles.TryGetValue(id, out var style))
            { Loss("style-missing", "Missing style definition", "Document defaults applied."); return (defaultText, defaultParagraph); }
            visited ??= [];
            if (!visited.Add(id) || visited.Count > 32)
            { Loss("style-cycle", "Cyclic or excessively deep style inheritance", "Document defaults applied.", style); return (defaultText, defaultParagraph); }
            var basis = ResolveStyle(Value(style.Element(W + "basedOn")), visited);
            return (ReadTextStyle(style.Element(W + "rPr"), basis.Text), ReadParagraphStyle(style.Element(W + "pPr"), basis.Paragraph, numbering));
        }
        bool HasOutline(string? id)
        {
            var visited = new HashSet<string>();
            while (id is not null && visited.Add(id) && styles.TryGetValue(id, out var style))
            {
                if (style.Element(W + "pPr")?.Element(W + "outlineLvl") is not null) return true;
                id = Value(style.Element(W + "basedOn"));
            }
            return false;
        }
        TextStyle CharacterStyle(string? id, TextStyle basis, HashSet<string>? visited = null)
        {
            if (id is null || !styles.TryGetValue(id, out var style)) return basis;
            visited ??= [];
            if (!visited.Add(id) || visited.Count > 32)
            { Loss("style-cycle", "Cyclic character style inheritance", "Inherited paragraph formatting retained.", style); return basis; }
            return ReadTextStyle(style.Element(W + "rPr"), CharacterStyle(Value(style.Element(W + "basedOn")), basis, visited));
        }
        string? ReadResource(string? relationshipId, string kind, XElement source)
        {
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var relationship) ||
                (string?)relationship.Attribute("Type") != R.NamespaceName + "/" + kind || (string?)relationship.Attribute("TargetMode") == "External")
            { Loss(kind + "-unavailable", "Missing, external or unsupported " + kind + " relationship", "Alternative text retained; no resource fetched.", source); return null; }
            var part = ResolvePart((string?)relationship.Attribute("Target") ?? "", currentPart);
            if (part is null || archive.GetEntry(part) is not { } entry)
            { Loss(kind + "-unavailable", "Missing or unsafe " + kind + " package part", "Alternative text retained.", source); return null; }
            var mediaType = (string?)contentTypes?.Elements(Ct + "Override").FirstOrDefault(e => (string?)e.Attribute("PartName") == "/" + part)?.Attribute("ContentType") ??
                (string?)contentTypes?.Elements(Ct + "Default").FirstOrDefault(e => string.Equals((string?)e.Attribute("Extension"), Path.GetExtension(part).TrimStart('.'), StringComparison.OrdinalIgnoreCase))?.Attribute("ContentType") ??
                (kind == "image" ? ImageMediaType(Path.GetExtension(part)) : "application/octet-stream");
            if (kind == "image" && ImageExtension(mediaType) is null)
            { Loss("image-format", "Unsupported image encoding", "Alternative text retained.", source); return null; }
            if (!imageResources.TryGetValue(part, out var resourceId))
            {
                if (entry.Length > DocumentResource.MaximumEmbeddedBytes || resourceBytes + entry.Length > DocumentResource.MaximumDocumentEmbeddedBytes)
                    throw new FormatException("DOCX embedded resources exceed resource limits.");
                var data = ReadPart(entry, (int)Math.Min(DocumentResource.MaximumEmbeddedBytes, DocumentResource.MaximumDocumentEmbeddedBytes - resourceBytes));
                resourceBytes += data.Length;
                resourceId = $"docx-resource-{imageResources.Count + 1}";
                imageResources.Add(part, resourceId);
                resources.Add(resourceId, new DocumentResource { MediaType = mediaType!, Data = ImmutableArray.CreateRange(data) });
            }
            return resourceId;
        }
        RichRun ReadDrawing(XElement drawing, TextStyle style)
        {
            var picture = drawing.Descendants(Wp + "docPr").FirstOrDefault();
            var alt = (string?)picture?.Attribute("descr") ?? (string?)picture?.Attribute("title") ?? "";
            if (alt.Length > 16384) throw new FormatException("DOCX image alternative text is too long.");
            var blip = drawing.Descendants(A + "blip").FirstOrDefault();
            var original = blip?.Descendants().FirstOrDefault(e => e.Name == Svg + "svgBlip" || e.Name == Tx + "original");
            var fallbackId = (string?)blip?.Attribute(R + "embed");
            var resourceId = ReadResource((string?)original?.Attribute(R + "embed") ?? fallbackId, "image", drawing);
            if (resourceId is null) return new RichRun(alt, style);
            var previewId = original is null ? null : ReadResource(fallbackId, "image", drawing);
            var extent = drawing.Descendants(Wp + "extent").FirstOrDefault();
            var width = Dimension(extent, "cx", 32 * 9525) / 9525;
            var height = Dimension(extent, "cy", 32 * 9525) / 9525;
            if (width is <= 0 or > 10000 || height is <= 0 or > 10000) throw new FormatException("Invalid DOCX image dimensions.");
            return new RichRun(new InlineDescriptor { AltText = alt, Width = width, Height = height,
                Placement = ReadImagePlacement(drawing), Payload = new ImageInlinePayload(resourceId) { PreviewResourceId = previewId } }, style);
        }
        RichRun ReadOle(XElement source, TextStyle style)
        {
            var shape = source.Element(V + "shape");
            var alt = (string?)shape?.Attribute("alt") ?? "Embedded object";
            var data = source.Element(O + "OLEObject");
            if (data is null || (string?)data.Attribute("Type") != "Embed")
            { Loss("ole-linked", "Linked or unsupported OLE object", "Alternative text retained; no linked data accessed.", source); return new RichRun(alt, style); }
            var relationId = (string?)data.Attribute(R + "id");
            var relationshipKind = relationId is not null && relationships.TryGetValue(relationId, out var relation) &&
                (string?)relation.Attribute("Type") == R.NamespaceName + "/package" ? OleRelationshipKind.Package : OleRelationshipKind.OleObject;
            var previewId = ReadResource((string?)shape?.Element(V + "imagedata")?.Attribute(R + "id"), "image", source);
            if (previewId is null) return new RichRun(alt, style);
            var resourceId = ReadResource(relationId, relationshipKind == OleRelationshipKind.Package ? "package" : "oleObject", source);
            if (resourceId is null) return new RichRun(alt, style);
            var width = Dimension(source, W + "dxaOrig", 480) / 15;
            var height = Dimension(source, W + "dyaOrig", 480) / 15;
            return new RichRun(new InlineDescriptor { AltText = alt, Width = width, Height = height,
                Placement = source.Attribute(Tx + "placement") is { } placement ? System.Text.Json.JsonSerializer.Deserialize<ImagePlacement>(placement.Value, JsonDocumentFormat.Options) : null,
                Payload = new OleInlinePayload(resourceId, previewId) { ProgramId = (string?)data.Attribute("ProgID") ?? "", FileName = (string?)source.Attribute(Tx + "fileName") ?? "",
                    RelationshipKind = relationshipKind } }, style);
        }
        RichRun ReadEquation(XElement source, TextStyle style)
        {
            var alt = EquationMarkup.AlternativeText(source);
            try
            {
                var xml = source.ToString(SaveOptions.DisableFormatting);
                EquationMarkup.Parse(xml);
                return new RichRun(new InlineDescriptor { AltText = alt, Width = 32, Height = 24,
                    Payload = new EquationInlinePayload(xml) }, style);
            }
            catch (FormatException)
            {
                Loss("equation-invalid", "Unsupported or oversized Office Math equation", "Equation text retained without its Math ML payload.", source);
                return new RichRun(alt, style);
            }
        }
        Paragraph ReadParagraph(XElement element)
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in element.Elements().Where(e => e.Annotation<FieldBoundary>() is null && e.Name != W + "pPr" && e.Name != W + "r" && e.Name != W + "hyperlink" &&
                e.Name != W + "ins" && e.Name != W + "del" && e.Name != W + "moveFrom" && e.Name != W + "moveTo" &&
                e.Name != W + "bookmarkStart" && e.Name != W + "bookmarkEnd" && e.Name != W + "proofErr" && e.Name != W + "fldSimple" && e.Name != W + "sdt" &&
                e.Name != W + "permStart" && e.Name != W + "permEnd" && e.Name != M + "oMath" && e.Name != M + "oMathPara" && e.Name.Namespace != Tx))
                Loss("paragraph-content", child.Name.LocalName, "Recognized run content retained; unsupported paragraph content omitted.", child);
            var pp = element.Element(W + "pPr");
            var styleId = Value(pp?.Element(W + "pStyle")) ?? defaultParagraphId;
            var basis = ResolveStyle(styleId);
            var paragraphStyle = ReadParagraphStyle(pp, basis.Paragraph, numbering);
            var paragraphText = ReadTextStyle(pp?.Element(W + "rPr"), basis.Text) with { Overrides = ReadTextOverrides(pp?.Element(W + "rPr")) };
            if (pp?.Element(W + "outlineLvl") is null && !HasOutline(styleId) && styleId?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(styleId[7..], out var heading) && heading is >= 1 and <= 6)
                paragraphStyle = paragraphStyle with { HeadingLevel = heading };
            var numId = Value(pp?.Element(W + "numPr")?.Element(W + "numId"));
            numId ??= numbering.FirstOrDefault(pair => pair.Value.Identity == paragraphStyle.ListId).Key;
            if (numId is not null && numbering.TryGetValue(numId, out var info) && startsUsed.Add((numId, paragraphStyle.ListLevel)) && info.Starts.TryGetValue(paragraphStyle.ListLevel, out var start))
                paragraphStyle = paragraphStyle with { ListStart = start, ListRestart = true };
            var paragraphId = Guid.NewGuid();
            var runs = new List<RichRun>();
            bool ReadRangeBoundary(XElement node)
            {
                var anchor = new DocumentAnchor { ParagraphId = paragraphId, Offset = runs.Sum(r => r.Storage.Length) };
                if (node.Name == Tx + "controlStart")
                {
                    var control = RangeInterchange.Decode<DocumentContentControl>((string?)node.Attribute("data") ?? "");
                    controlStarts[(currentPart, (string?)node.Attribute("id") ?? "")] = control with { Start = anchor with { Affinity = control.Start.Affinity } };
                    return true;
                }
                if (node.Name == Tx + "controlEnd")
                {
                    if (!controlStarts.Remove((currentPart, (string?)node.Attribute("id") ?? ""), out var control)) throw new FormatException("Unmatched content-control boundary.");
                    contentControls.Add(control with { End = anchor with { Affinity = control.End.Affinity } }); return true;
                }
                if (node.Name == W + "permStart" || node.Name == Tx + "permissionStart")
                {
                    var permission = node.Attribute(Tx + "permission") is { } encoded ? RangeInterchange.Decode<DocumentPermissionRange>(encoded.Value) :
                        new DocumentPermissionRange { Start = anchor, End = anchor, User = (string?)node.Attribute(W + "ed"), Group = (string?)node.Attribute(W + "edGrp") };
                    if (node.Attribute(W + "colFirst") is not null || node.Attribute(W + "colLast") is not null)
                    {
                        permission = permission with { IsReadOnly = true, User = null, Group = null };
                        Loss("permission-columns", "Column-scoped table permission", "Applied read-only restrictions to the enclosing range conservatively; column-specific edit exceptions are unavailable.", node);
                    }
                    if (!permissionStarts.TryAdd((currentPart, (string?)node.Attribute(W + "id") ?? ""), permission with { Start = anchor with { Affinity = permission.Start.Affinity } }))
                        throw new FormatException("Duplicate permission boundary.");
                    return true;
                }
                if (node.Name == W + "permEnd" || node.Name == Tx + "permissionEnd")
                {
                    if (!permissionStarts.Remove((currentPart, (string?)node.Attribute(W + "id") ?? ""), out var permission)) throw new FormatException("Unmatched permission boundary.");
                    permissionRanges.Add(permission with { End = anchor with { Affinity = permission.End.Affinity } }); return true;
                }
                if (node.Annotation<FieldBoundary>() is { } boundary)
                {
                    if (boundary.Start) { boundary.Field.Field = boundary.Field.Field with { Start = anchor with { Affinity = boundary.Field.Field.Start.Affinity } }; boundary.Field.Started = true; }
                    else if (boundary.Field.Started)
                    {
                        var field = boundary.Field.Field with { End = anchor with { Affinity = boundary.Field.Field.End.Affinity } };
                        generalFields.Add(field); RangeInterchange.DiagnoseField(field.Instruction, "docx", field.Id);
                    }
                    return true;
                }
                if (node.Name == W + "bookmarkStart")
                {
                    var key = (currentPart, (string?)node.Attribute(W + "id") ?? "");
                    var name = (string?)node.Attribute(W + "name") ?? "";
                    if (name.Length == 0) { Loss("bookmark", "Unnamed bookmark", "Omitted the unnamed marker.", node); return true; }
                    var original = name; var suffix = 2;
                    while (!bookmarkNames.Add(name)) name = original + "_" + suffix++;
                    if (name != original) Loss("bookmark-name", "Duplicate bookmark name", "Renamed the imported bookmark to " + name + ".", node);
                    var metadata = node.Attribute(Tx + "bookmark") is { } data ? RangeInterchange.Decode<DocumentBookmark>(data.Value) : new DocumentBookmark();
                    if (bookmarkStarts.ContainsKey(key)) Loss("bookmark", "Duplicate bookmark start ID", "Kept the most recent start marker.", node);
                    bookmarkStarts[key] = metadata with { Name = name, Start = anchor with { Affinity = metadata.Start.Affinity } };
                    return true;
                }
                if (node.Name == W + "bookmarkEnd")
                {
                    var key = (currentPart, (string?)node.Attribute(W + "id") ?? "");
                    if (bookmarkStarts.Remove(key, out var bookmark)) bookmarks.Add(bookmark with { End = anchor with { Affinity = bookmark.End.Affinity } });
                    else Loss("bookmark", "Unmatched bookmark end", "Omitted the detached marker.", node);
                    return true;
                }
                return false;
            }
            var fields = new Stack<ImportedField>();
            void AddRun(RichRun value)
            {
                if (fields.Count == 0) runs.Add(value);
                else if (fields.Peek().Separated) fields.Peek().Results.Add(value);
                else
                {
                    fields.Peek().Invalid = true;
                    Loss("field-structure", "Field result before its separator", "Retained visible text without active field semantics.", fields.Peek().Source);
                    fields.Peek().Results.Add(value);
                }
            }
            void BeginField(XElement source, TextStyle style, string? instruction = null)
            {
                if (fields.Count >= 32) throw new FormatException("DOCX field nesting exceeds the limit.");
                var field = new ImportedField(source, style, instruction);
                if (fields.Count > 0)
                {
                    field.Invalid = true;
                    foreach (var parent in fields) parent.Invalid = true;
                    Loss("nested-field", "Nested Word fields", "Retained cached display text without active field semantics.", source);
                }
                fields.Push(field);
            }
            void EndField(XElement source, bool unmatched = false)
            {
                if (fields.Count == 0)
                {
                    Loss("field-structure", "Unmatched field end", "Omitted the marker and retained surrounding text.", source);
                    return;
                }
                var field = fields.Pop();
                if (unmatched)
                {
                    field.Invalid = true;
                    Loss("field-structure", "Unclosed or cross-paragraph field", "Retained cached display text without active field semantics.", field.Source);
                }
                if (PageFieldInstructions.TryParse(field.Instruction.ToString(), out var pageField) && !field.Invalid)
                {
                    var display = string.Concat(field.Results.Select(r => r.PlainText));
                    if (field.Results.Any(r => r.Inline is not null) || display.Length > 16384)
                    {
                        Loss("field-result", "Non-text or oversized page-field result", "Retained result content without active field semantics.", field.Source);
                        foreach (var result in field.Results) AddRun(result);
                        return;
                    }
                    var style = field.Results.FirstOrDefault()?.Style ?? field.Style;
                    if (field.Results.Any(r => r.Style != style))
                        Loss("field-result-formatting", "Multiple styles in an atomic page field", "Applied the first character style to the field display.", field.Source);
                    AddRun(new RichRun(InlineDescriptor.PageField(pageField) with { AltText = display.Length == 0 ? "1" : display }, style));
                    return;
                }
                var parsed = MergeFieldInstructions.Parse(field.Instruction.ToString());
                if (parsed is null)
                    Loss("field", "Unsupported or malformed Word field instruction", "Cached display text retained without active field semantics.", field.Source);
                var cached = string.Concat(field.Results.Select(r => r.PlainText));
                if (field.Results.Any(r => r.Inline is not null) || cached.Length > 16_384)
                {
                    field.Invalid = true;
                    Loss("field-result", "Non-text or oversized field result", "Retained the result content without active field semantics.", field.Source);
                }
                if (parsed is not null && !field.Invalid)
                {
                    if (parsed.UnsupportedSwitches)
                        Loss("merge-field-switch", "Unsupported merge-field switches", "Retained the field name and cached display without the switch behavior.", field.Source);
                    var style = parsed.CharacterFormat && field.HasInstructionStyle ? field.Style : field.Results.FirstOrDefault()?.Style ?? field.ResultStyle ?? field.Style;
                    if (field.Results.Any(r => r.Style != style))
                        Loss("field-result-formatting", "Multiple styles in an atomic merge field", "Applied one character style to the field display.", field.Source);
                    if (fields.Count == 0 || fields.Peek().Separated) AddRun(MergeFieldInstructions.Create(parsed.Name, cached, style));
                }
                else if (fields.Count == 0 || fields.Peek().Separated)
                    foreach (var result in field.Results) AddRun(result);
            }
            void ReadInline(XElement node)
            {
                if (ReadRangeBoundary(node)) return;
                if (node.Name == W + "del" || node.Name == W + "moveFrom" || node.Name == W + "pPr" || node.Name == W + "p") return;
                if (node.Name == M + "oMath" || node.Name == M + "oMathPara")
                { AddRun(ReadEquation(node, paragraphText)); return; }
                if (node.Name == W + "sdt")
                {
                    var control = ReadContentControl(node, name => placeholderTexts.GetValueOrDefault(name));
                    var start = new DocumentAnchor { ParagraphId = paragraphId, Offset = runs.Sum(r => r.Storage.Length), Affinity = control.Start.Affinity };
                    if (control.IsAtomic)
                    {
                        if (node.Element(W + "sdtContent")?.Descendants().Any(e => e.Name == W + "sdt" || e.Name == W + "permStart" || e.Name == W + "permEnd") == true)
                            throw new FormatException("An atomic content control cannot contain nested controls or permissions.");
                        if (node.Element(W + "sdtContent")?.Descendants().Any(e => e.Name == W + "drawing" || e.Name == W + "object" || e.Annotation<FieldBoundary>() is not null) == true)
                            Loss("atomic-control-content", "Rich object or dynamic field inside an atomic form control", "Retained the typed form state and text display; embedded result semantics omitted.", node, control.Id);
                        if (fields.Count != 0) throw new FormatException("Atomic form controls inside atomic fields are unsupported.");
                        var controlStyle = ReadTextStyle(node.Element(W + "sdtContent")?.Descendants(W + "rPr").FirstOrDefault(), paragraphText);
                        AddRun(new RichRun(new InlineDescriptor { AltText = ControlDisplay(control), Payload = new FormControlInlinePayload(control.Id) }, controlStyle));
                    }
                    else if (!On(node.Element(W + "sdtPr")?.Element(W + "showingPlcHdr")))
                        foreach (var child in node.Element(W + "sdtContent")?.Elements() ?? []) ReadInline(child);
                    control = control with { Start = start, End = new DocumentAnchor { ParagraphId = paragraphId, Offset = runs.Sum(r => r.Storage.Length), Affinity = control.End.Affinity } };
                    contentControls.Add(control); return;
                }
                if (node.Name == W + "fldSimple")
                {
                    BeginField(node, paragraphText, (string?)node.Attribute(W + "instr") ?? "");
                    foreach (var child in node.Elements()) ReadInline(child);
                    while (fields.Count > 0 && fields.Peek().Source != node) EndField(node, unmatched: true);
                    EndField(node);
                    return;
                }
                if (node.Name != W + "r")
                {
                    foreach (var child in node.Elements()) ReadInline(child);
                    return;
                }
                var run = node;
                var rp = run.Element(W + "rPr");
                var characterId = Value(rp?.Element(W + "rStyle"));
                if (characterId is not null && !catalog.Characters.ContainsKey(characterId))
                { Loss("style-missing", "Missing character style definition", "Paragraph formatting retained.", rp); characterId = null; }
                var style = ReadTextStyle(rp, CharacterStyle(characterId, basis.Text)) with { StyleId = characterId, Overrides = ReadTextOverrides(rp) };
                var hyperlink = run.Ancestors(W + "hyperlink").FirstOrDefault();
                if (hyperlink is not null)
                {
                    var relationId = (string?)hyperlink.Attribute(R + "id") ?? "";
                    var link = relationships.TryGetValue(relationId, out var relation) ? (string?)relation.Attribute("Target") : null;
                    if ((string?)hyperlink.Attribute(W + "anchor") is { Length: > 0 } bookmarkName)
                    {
                        var destination = new InternalLinkDestination { BookmarkName = bookmarkName, Tooltip = (string?)hyperlink.Attribute(W + "tooltip"),
                            Activation = Enum.TryParse<InternalLinkActivation>((string?)hyperlink.Attribute(Tx + "activation"), out var activation) ? activation : InternalLinkActivation.ModifierClick };
                        style = style with { InternalLink = destination, Overrides = style.Overrides! with { InternalLink = destination } };
                    }
                    else if (link is not null && FlowDocument.IsSafeHyperlink(link)) style = style with { Hyperlink = link, Overrides = style.Overrides! with { Hyperlink = link } };
                    else Loss("hyperlink", "Unsafe or internal hyperlink", "Link text retained without navigation.", hyperlink);
                }
                if (fields.Count > 0 && fields.Peek().Separated) fields.Peek().ResultStyle ??= style;
                var consumedCustomMarks = new HashSet<XElement>();
                foreach (var content in run.Elements().Where(e => e.Name != W + "rPr"))
                {
                    if (ReadRangeBoundary(content) || consumedCustomMarks.Contains(content)) continue;
                    if (content.Name == W + "footnoteReference" || content.Name == W + "endnoteReference")
                    {
                        var key = (content.Name == W + "endnoteReference", (string?)content.Attribute(W + "id") ?? "");
                        if (!notes.TryGetValue(key, out var note))
                        { Loss("note-missing", "Missing footnote or endnote story", "Reference omitted.", content); continue; }
                        if (currentPart != "word/document.xml")
                        { Loss("nested-note", "Note reference outside the main story", "Reference omitted; surrounding story content retained.", content); continue; }
                        if (!referencedNotes.Add(note.Id))
                        { Loss("note-reference-duplicate", "Multiple references to one note", "Subsequent reference retained as text.", content); AddRun(new RichRun(note.CustomMark ?? "*", style)); continue; }
                        if (OnAttribute(content, "customMarkFollows"))
                        {
                            var mark = content.ElementsAfterSelf().FirstOrDefault();
                            if (mark?.Name == W + "t" && mark.Value.Length is > 0 and <= 32 && !string.IsNullOrWhiteSpace(mark.Value) && !mark.Value.Any(char.IsControl))
                            { note = note with { CustomMark = mark.Value }; notes[key] = note; consumedCustomMarks.Add(mark); }
                            else Loss("note-custom-mark", "Custom note mark outside the reference run", "Automatic reference numbering applied.", content);
                        }
                        AddRun(new RichRun(InlineDescriptor.Note(note.Id, note.CustomMark ?? "1"), style));
                    }
                    else if (content.Name == W + "footnoteRef" || content.Name == W + "endnoteRef") { }
                    else if (content.Name == W + "drawing") AddRun(ReadDrawing(content, style));
                    else if (content.Name == W + "object") AddRun(ReadOle(content, style));
                    else if (content.Name == M + "oMath" || content.Name == M + "oMathPara") AddRun(ReadEquation(content, style));
                    else if (content.Name == W + "pict" && content.Descendants().Any(e => e.Attribute(Tx + "watermark") is not null)) { }
                    else if (content.Name == W + "t") AddRun(new RichRun(content.Value.Replace('\n', '\u2028').Replace("\r", ""), style));
                    else if (content.Name == W + "tab") AddRun(new RichRun("\t", style));
                    else if (content.Name == W + "br" || content.Name == W + "cr")
                    {
                        if ((string?)content.Attribute(W + "type") is "page" or "column") Loss("page-break", "Page or column break", "Soft line break retained.", content);
                        AddRun(new RichRun("\u2028", style));
                    }
                    else if (content.Name == W + "instrText")
                    {
                        if (fields.Count == 0 || fields.Peek().Separated)
                        {
                            if (fields.Count > 0) fields.Peek().Invalid = true;
                            Loss("field-structure", "Field instruction outside the instruction span", "Omitted the instruction and retained visible text.", content);
                        }
                        else
                        {
                            var field = fields.Peek(); field.Instruction.Append(content.Value);
                            if (!field.HasInstructionStyle && !string.IsNullOrWhiteSpace(content.Value))
                            { field.Style = style; field.HasInstructionStyle = true; }
                        }
                    }
                    else if (content.Name == W + "fldChar")
                    {
                        var kind = (string?)content.Attribute(W + "fldCharType");
                        if (kind == "begin") BeginField(content, style);
                        else if (kind == "end" && (fields.Count == 0 || !fields.Peek().Simple)) EndField(content);
                        else if (kind == "separate" && fields.Count > 0 && !fields.Peek().Separated) fields.Peek().Separated = true;
                        else
                        {
                            if (fields.Count > 0) fields.Peek().Invalid = true;
                            Loss("field-structure", "Invalid or unmatched field marker", "Retained visible text without active field semantics.", content);
                        }
                    }
                    else if (content.Name != W + "lastRenderedPageBreak") Loss("run-content", content.Name.LocalName, "Unsupported run content omitted.", content);
                }
            }
            foreach (var child in element.Elements()) ReadInline(child);
            while (fields.Count > 0) EndField(element, unmatched: true);
            var paragraphOverrides = ReadParagraphOverrides(pp, numbering);
            if (pp?.Element(W + "tabs") is not null) paragraphOverrides = paragraphOverrides with { TabStops = paragraphStyle.TabStops };
            if (paragraphStyle.ListRestart) paragraphOverrides = paragraphOverrides with { ListStart = new(paragraphStyle.ListStart), ListRestart = true };
            if (pp?.Element(W + "outlineLvl") is null && !HasOutline(styleId) && paragraphStyle.HeadingLevel > 0)
                paragraphOverrides = paragraphOverrides with { HeadingLevel = paragraphStyle.HeadingLevel };
            paragraphStyle = paragraphStyle with { StyleId = styleId is not null && catalog.Paragraphs.ContainsKey(styleId) ? styleId : null, Overrides = paragraphOverrides };
            var result = new Paragraph(runs) { Id = paragraphId, Style = paragraphStyle, DefaultStyle = paragraphText };
            paragraphSources[element] = result.Id;
            return result;
        }
        Table ReadTable(XElement element, int depth)
        {
            var rows = element.Elements(W + "tr").ToArray();
            var grid = element.Element(W + "tblGrid")?.Elements(W + "gridCol").ToArray() ?? [];
            if (rows.Length is < 1 or > 1000 || grid.Length > 100) throw new FormatException("DOCX table exceeds model grid limits.");
            var rowWidths = rows.Select(row =>
            {
                var before = Number(row.Element(W + "trPr")?.Element(W + "gridBefore"));
                var after = Number(row.Element(W + "trPr")?.Element(W + "gridAfter"));
                if (before is < 0 or > 100 || after is < 0 or > 100) throw new FormatException("Invalid DOCX grid offset.");
                long width = (long)before + after;
                foreach (var cell in row.Elements(W + "tc"))
                {
                    var span = Number(cell.Element(W + "tcPr")?.Element(W + "gridSpan"), 1);
                    if (span is < 1 or > 100) throw new FormatException("Invalid DOCX grid span.");
                    width += span;
                    if (width > 100) throw new FormatException("DOCX table exceeds model grid limits.");
                }
                return (int)width;
            }).ToArray();
            var columns = Math.Max(grid.Length, rowWidths.Max());
            if (columns is < 1 or > 100) throw new FormatException("DOCX table exceeds model grid limits.");
            var table = Table.Create(rows.Length, columns);
            if (grid.Length == columns && grid.All(c => Dimension(c, W + "w", 0) > 0))
                table = table with { ColumnWidths = grid.Select(c => Dimension(c, W + "w", 0) / 15).ToImmutableArray() };

            var sizing = rows.Select(r => r.Element(W + "trPr")).Select(properties => { var h = properties?.Element(W + "trHeight"); return new TableRowSizing
            {
                Mode = h is null || Number(h) <= 0 ? TableRowHeightMode.Auto : (string?)h.Attribute(W + "hRule") == "exact" ? TableRowHeightMode.Exact : TableRowHeightMode.AtLeast,
                Height = h is null ? 0 : Math.Max(0, Number(h) / 15d), AllowSplit = !On(properties?.Element(W + "cantSplit"))
            }; }).ToImmutableArray();
            if (sizing.Any(s => s.Mode != TableRowHeightMode.Auto || !s.AllowSplit)) table = table with { RowSizing = sizing };
            var tblPr = element.Element(W + "tblPr");
            var tableStyleId = Value(tblPr?.Element(W + "tblStyle"));
            if (tableStyleId is not null && catalog.Tables.ContainsKey(tableStyleId)) table = table with { StyleId = tableStyleId };
            else if (tableStyleId is not null) Loss("style-missing", "Missing table style definition", "Direct table formatting retained.", tblPr);
            table = table with { StyleOverrides = ReadTableFormatting(tblPr) };
            table = ReadTableProperties(table, tblPr, rows);
            var tablePadding = ReadPadding(tblPr?.Element(W + "tblCellMar"));
            foreach (var property in tblPr?.Elements() ?? [])
                if (property.Name != W + "tblW" && property.Name != W + "tblBorders" && property.Name != W + "tblCellMar" && property.Name != W + "tblStyle" && property.Name != W + "shd" && property.Name != W + "tblLayout" && property.Name != W + "jc" &&
                    property.Name != W + "tblInd" && property.Name != W + "bidiVisual" && property.Name != W + "tblLook" && property.Name != W + "tblpPr")
                    Loss("table-property", property.Name.LocalName, "Table grid and direct cell formatting retained.", property);
            var vertical = new Dictionary<int, (int Row, int Column, int Span)>();
            for (var r = 0; r < rows.Length; r++)
            {
                token.ThrowIfCancellationRequested();
                var column = Number(rows[r].Element(W + "trPr")?.Element(W + "gridBefore"));
                if (column < 0) throw new FormatException("Invalid DOCX grid offset.");
                var nextVertical = new Dictionary<int, (int, int, int)>();
                foreach (var cell in rows[r].Elements(W + "tc"))
                {
                    var properties = cell.Element(W + "tcPr");
                    foreach (var property in properties?.Elements() ?? [])
                        if (property.Name != W + "tcW" && property.Name != W + "gridSpan" && property.Name != W + "vMerge" && property.Name != W + "hMerge" &&
                            property.Name != W + "shd" && property.Name != W + "tcMar" && property.Name != W + "tcBorders" && property.Name != W + "vAlign" && property.Name != W + "textDirection")
                            Loss("cell-property", property.Name.LocalName, "Cell content, sizing, and supported direct formatting retained.", property);
                    var span = Number(properties?.Element(W + "gridSpan"), 1);
                    if (span is < 1 or > 100 || column > columns - span) throw new FormatException("Invalid DOCX grid span.");
                    var merge = properties?.Element(W + "vMerge");
                    if (merge is not null && Value(merge) != "restart")
                    {
                        if (!vertical.TryGetValue(column, out var owner) || owner.Span != span) throw new FormatException("DOCX vertical merge has no matching anchor.");
                        var anchor = table.Rows[owner.Row][owner.Column];
                        table = table.SetCell(owner.Row, owner.Column, anchor with { RowSpan = r - owner.Row + 1 });
                        nextVertical[column] = owner;
                        if (cell.Descendants(W + "t").Any(t => t.Value.Length != 0)) Loss("merge-continuation", "Content in a covered merge cell", "Anchor content retained.", cell);
                    }
                    else
                    {
                        var blocks = ReadBlocks(cell.Elements(), depth + 1).ToImmutableArray();
                        var borders = ReadBorders(properties?.Element(W + "tcBorders"));
                        table = table.SetCell(r, column, new TableCell
                        {
                            Blocks = blocks.IsEmpty ? [new Paragraph()] : blocks, ColumnSpan = span,
                            Background = ReadColor((string?)properties?.Element(W + "shd")?.Attribute(W + "fill")),
                            Padding = ReadPadding(properties?.Element(W + "tcMar")) ?? tablePadding, Borders = borders,
                            PreferredWidth = ReadPreferredWidth(properties?.Element(W + "tcW")), VerticalAlignment = ReadCellVerticalAlignment(properties),
                            TextDirection = ReadCellTextDirection(properties),
                            StyleOverrides = Value(properties?.Element(W + "vAlign")) == "top" ? new() { VerticalAlignment = TableCellVerticalAlignment.Top } : null
                        });
                        if (merge is not null) nextVertical[column] = (r, column, span);
                    }
                    if (properties?.Element(W + "hMerge") is { } horizontal) Loss("legacy-horizontal-merge", "Legacy horizontal merge", "Cell retained; gridSpan merges are supported.", horizontal);
                    column += span;
                }
                vertical = nextVertical;
            }
            return table;
        }
        IEnumerable<Block> ReadBlocks(IEnumerable<XElement> elements, int depth)
        {
            if (depth > 32) throw new FormatException("DOCX block nesting exceeds the limit.");
            foreach (var element in elements)
            {
                token.ThrowIfCancellationRequested();
                if (element.Name == W + "p")
                {
                    if (Value(element.Element(W + "pPr")?.Element(W + "pStyle")) == CellEndStyle && !element.Elements(W + "r").Any()) continue;
                    yield return ReadParagraph(element);
                }
                else if (element.Name == W + "tbl") yield return ReadTable(element, depth);
                else if (element.Name == W + "sdt")
                {
                    var children = ReadBlocks(element.Element(W + "sdtContent")?.Elements() ?? [], depth + 1).ToImmutableArray();
                    if (Value(element.Element(W + "sdtPr")?.Element(W + "tag")) == SectionTag &&
                        (string?)element.Element(W + "sdtPr")?.Attribute(Tx + "decorativeSection") == "1")
                    { if (!children.IsEmpty) yield return new Section { Blocks = children, Padding = 0 }; continue; }
                    var control = ReadContentControl(element, name => placeholderTexts.GetValueOrDefault(name));
                    children = FlowDocument.EnsureBlocks(children);
                    var entries = new DocumentIndex(new FlowDocument(children)).Paragraphs;
                    if (control.IsAtomic)
                    {
                        if (entries.Length != 1 || contentControls.Any(c => entries.Any(p => p.Paragraph.Id == c.Start.ParagraphId)))
                            throw new FormatException("Block atomic form control must contain one paragraph without nested controls.");
                        var first = entries[0].Paragraph;
                        var paragraph = first with { Runs = [new RichRun(new InlineDescriptor { AltText = ControlDisplay(control), Payload = new FormControlInlinePayload(control.Id) }, first.Runs.FirstOrDefault()?.Style ?? first.DefaultStyle)] };
                        control = control with { Start = new DocumentAnchor { ParagraphId = paragraph.Id, Affinity = control.Start.Affinity }, End = new DocumentAnchor { ParagraphId = paragraph.Id, Offset = 1, Affinity = control.End.Affinity } };
                        children = [paragraph];
                    }
                    else control = control with { Start = new DocumentAnchor { ParagraphId = entries[0].Paragraph.Id, Affinity = control.Start.Affinity },
                        End = new DocumentAnchor { ParagraphId = entries[^1].Paragraph.Id, Offset = entries[^1].Paragraph.Length, Affinity = control.End.Affinity } };
                    contentControls.Add(control);
                    foreach (var child in children) yield return child;
                }
                else if (element.Name == W + "ins" || element.Name == W + "moveTo" || element.Name == W + "customXml")
                { foreach (var block in ReadBlocks(element.Elements(), depth)) yield return block; }
                else if (element.Name != W + "sectPr" && element.Name != W + "tcPr" && element.Name != W + "del" && element.Name != W + "moveFrom" && element.Name != W + "bookmarkStart" && element.Name != W + "bookmarkEnd")
                    Loss("block-content", element.Name.LocalName, "Unsupported block omitted.", element);
            }
        }
        var body = Xml("word/document.xml", true)?.Root?.Element(W + "body") ?? throw new FormatException("Missing DOCX document body.");
        foreach (var revision in body.Descendants().Where(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "moveTo" || e.Name.LocalName.EndsWith("PrChange", StringComparison.Ordinal)))
            Loss("revision", "Tracked revision history", "Current accepted content retained; deleted content and history omitted.", revision);
        ImmutableArray<Block> ReadStoryBlocks(string part, XElement root)
        {
            var previousRelationships = relationships; var previousPart = currentPart;
            currentPart = part;
            var slash = part.LastIndexOf('/');
            var relationPart = part[..(slash + 1)] + "_rels/" + part[(slash + 1)..] + ".rels";
            relationships = Xml(relationPart)?.Root?.Elements(Rel + "Relationship").Where(e => e.Attribute("Id") is not null)
                .ToDictionary(e => (string)e.Attribute("Id")!, e => e) ?? [];
            try { return FlowDocument.EnsureBlocks(ReadBlocks(root.Elements(), 0).ToImmutableArray()); }
            finally { relationships = previousRelationships; currentPart = previousPart; }
        }
        foreach (var endnote in new[] { false, true })
        {
            var kind = endnote ? "endnotes" : "footnotes";
            var relation = mainRelationships.Values.FirstOrDefault(e => (string?)e.Attribute("Type") == R.NamespaceName + "/" + kind);
            if (relation is null) continue;
            if ((string?)relation.Attribute("TargetMode") == "External")
            { Loss("note-external", "External note relationship", "Notes omitted; no resource fetched.", relation); continue; }
            var part = ResolvePart((string?)relation.Attribute("Target") ?? "");
            if (part is null || Xml(part)?.Root is not { } root)
            { Loss("note-missing", "Missing or unsafe note part", "Notes omitted.", relation); continue; }
            foreach (var element in root.Elements(W + (endnote ? "endnote" : "footnote")))
            {
                var type = (string?)element.Attribute(W + "type");
                if (type is "separator" or "continuationSeparator")
                {
                    var text = string.Concat(element.Descendants(W + "t").Select(e => e.Value));
                    var noteSettings = endnote ? endnoteSettings : footnoteSettings;
                    if (text.Length > 256) { Loss("note-separator", "Oversized separator text", "Default separator retained.", element); continue; }
                    if (element.Descendants(W + "tbl").Any() || element.Descendants(W + "drawing").Any() || element.Descendants(W + "rPr").Any())
                        Loss("note-separator-formatting", "Rich note separator", "Separator text retained without rich formatting.", element);
                    if (text.Length > 0 || !element.Descendants().Any(e => e.Name == W + "separator" || e.Name == W + "continuationSeparator"))
                        noteSettings = type == "separator" ? noteSettings with { SeparatorText = text } : noteSettings with { ContinuationSeparatorText = text };
                    if (endnote) endnoteSettings = noteSettings; else footnoteSettings = noteSettings;
                    continue;
                }
                if (type is not (null or "normal"))
                { Loss("note-type", "Unsupported note type", "Unsupported note omitted.", element); continue; }
                var identity = (string?)element.Attribute(W + "id") ?? throw new FormatException("Missing DOCX note ID.");
                var story = new DocumentStory { Kind = endnote ? DocumentStoryKind.Endnote : DocumentStoryKind.Footnote, Blocks = ReadStoryBlocks(part, element) };
                var note = new DocumentNote { StoryId = story.Id, Kind = endnote ? DocumentNoteKind.Endnote : DocumentNoteKind.Footnote };
                if (!notes.TryAdd((endnote, identity), note)) throw new FormatException("Duplicate DOCX note ID.");
                stories.Add(story.Id, story);
                var prefix = element.Descendants(W + "r").FirstOrDefault();
                if (prefix is not null && !prefix.Elements(W + (endnote ? "endnoteRef" : "footnoteRef")).Any())
                    notePrefixText[note.Id] = string.Concat(prefix.Elements(W + "t").Select(t => t.Value));
            }
        }
        // Enumerate before freezing resources, which are populated while reading inline drawings.
        var blocks = ReadBlocks(body.Elements(), 0).ToArray();
        var importedStoryParts = new Dictionary<(string, bool), Guid>();
        var watermarkHeaderStories = new Dictionary<string, Guid>();
        StoryReference ReadHeaderFooter(XElement reference, bool footer)
        {
            var relationshipId = (string?)reference.Attribute(R + "id") ?? "";
            if (!mainRelationships.TryGetValue(relationshipId, out var relation) || (string?)relation.Attribute("TargetMode") == "External" ||
                (string?)relation.Attribute("Type") != R.NamespaceName + (footer ? "/footer" : "/header"))
            { Loss("header-footer-reference", "Missing or external header/footer relationship", "Unlinked empty story applied; no resource fetched.", reference); return new() { LinkToPrevious = false }; }
            var part = ResolvePart((string?)relation.Attribute("Target") ?? "");
            if (part is null) { Loss("header-footer-reference", "Unsafe header/footer part", "Unlinked empty story applied.", reference); return new() { LinkToPrevious = false }; }
            if (!importedStoryParts.TryGetValue((part, footer), out var identity))
            {
                var root = Xml(part)?.Root;
                if (root is null) { Loss("header-footer-reference", "Missing header/footer part", "Unlinked empty story applied.", reference); return new() { LinkToPrevious = false }; }
                if (root.Attribute(Tx + "watermarkHeader") is { } originalHeader)
                {
                    if (originalHeader.Value == "none") return new() { LinkToPrevious = false };
                    if (watermarkHeaderStories.TryGetValue(originalHeader.Value, out var existingStory)) return new() { StoryId = existingStory, LinkToPrevious = false };
                }
                root.Elements(W + "p").Where(paragraph => paragraph.Descendants().Any(element => element.Attribute(Tx + "watermark") is not null)).Remove();
                var story = new DocumentStory { Kind = footer ? DocumentStoryKind.Footer : DocumentStoryKind.Header, Blocks = ReadStoryBlocks(part, root) };
                if (root.Attribute(Tx + "watermarkHeader") is { } key) watermarkHeaderStories[key.Value] = story.Id;
                stories.Add(story.Id, story); identity = story.Id; importedStoryParts.Add((part, footer), identity);
            }
            return new() { StoryId = identity, LinkToPrevious = false };
        }
        var sections = ImmutableArray.CreateBuilder<DocumentSection>();
        var visibleParagraphs = new DocumentIndex(new FlowDocument(blocks)).Paragraphs.Select(p => p.Paragraph.Id).ToArray();
        var sectionStart = Guid.Empty;
        foreach (var properties in body.Descendants(W + "sectPr").Where(p => !p.Ancestors(W + "tbl").Any()))
        {
            if (!properties.HasElements && properties.Parent == body && sections.Count == 0) continue;
            var section = ReadPhysicalSection(properties, sectionStart, On(settings?.Root?.Element(W + "evenAndOddHeaders")), ReadHeaderFooter);
            section = section with { PageSettings = section.PageSettings with { MirrorMargins = On(settings?.Root?.Element(W + "mirrorMargins")) } };
            if (properties.Attribute(Tx + "headerLinks") is { } links)
            {
                var values = links.Value.Split(',');
                foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
                    if ((int)variant < values.Length && values[(int)variant] == "1")
                        section = section with { HeaderFooter = section.HeaderFooter.WithReference(false, variant, new StoryReference { LinkToPrevious = true }) };
            }
            if (properties.Element(Tx + "watermark") is { } watermarkElement)
            {
                var watermark = System.Text.Json.JsonSerializer.Deserialize<DocumentWatermark>((string?)watermarkElement.Attribute("data") ?? "", JsonDocumentFormat.Options)
                    ?? throw new FormatException("Invalid DOCX watermark data.");
                if (watermark.ResourceId is not null)
                    watermark = watermark with { ResourceId = ReadResource((string?)watermarkElement.Attribute(R + "embed"), "image", watermarkElement) };
                if (watermark.ResourceId is not null || watermark.Text is not null) { watermark.Validate(); section = section with { Watermark = watermark }; }
            }
            sections.Add(section);
            if (properties.Parent?.Parent is { } p && p.Name == W + "p")
            {
                if (!paragraphSources.TryGetValue(p, out var paragraphId))
                {
                    var nextSource = p.ElementsAfterSelf().SelectMany(e => e.DescendantsAndSelf(W + "p")).FirstOrDefault(e => paragraphSources.ContainsKey(e) && !e.Ancestors(W + "tbl").Any());
                    sectionStart = nextSource is null ? Guid.Empty : paragraphSources[nextSource];
                    if (sectionStart == Guid.Empty) break;
                    continue;
                }
                var position = Array.IndexOf(visibleParagraphs, paragraphId);
                if (position >= 0 && position + 1 < visibleParagraphs.Length)
                {
                    sectionStart = visibleParagraphs[position + 1];
                    if (!DocumentSection.IsOutsideTable(blocks, sectionStart))
                    {
                        var boundary = new Paragraph() { Style = new() { SpaceAfter = 0 } };
                        blocks = InsertTableSectionBoundary(blocks.ToImmutableArray(), sectionStart, boundary).ToArray();
                        visibleParagraphs = new DocumentIndex(new FlowDocument(blocks)).Paragraphs.Select(p => p.Paragraph.Id).ToArray();
                        sectionStart = boundary.Id;
                        Loss("section-table-boundary", "Section beginning directly with a table", "Inserted an empty boundary paragraph before the table to retain section ownership.", properties);
                    }
                }
                else { sectionStart = Guid.Empty; break; }
            }
        }
        if (sections.Count > 0 && sectionStart != Guid.Empty && body.Element(W + "sectPr") is null)
            sections.Add(new DocumentSection { StartParagraphId = sectionStart });
        foreach (var note in notes.Values.Where(n => n.CustomMark is not null && referencedNotes.Contains(n.Id)))
        {
            var story = stories[note.StoryId];
            if (notePrefixText.GetValueOrDefault(note.Id) == note.CustomMark)
                stories[story.Id] = story with { Blocks = RemoveNotePrefix(story.Blocks, note.CustomMark!) };
        }
        foreach (var note in notes.Values.Where(n => !referencedNotes.Contains(n.Id)))
        { Loss("note-unreferenced", "Note without a main-story reference", "Unused note story omitted."); stories.Remove(note.StoryId); }
        var customPropertiesXml = Xml("docProps/custom.xml");
        var document = new FlowDocument(blocks) { Resources = resources.ToImmutable(), Styles = catalog, Theme = theme, Fonts = fonts,
            Stories = stories.ToImmutable(), Notes = notes.Values.Where(n => referencedNotes.Contains(n.Id)).ToImmutableArray(), Sections = sections.ToImmutable(),
            FootnoteSettings = footnoteSettings, EndnoteSettings = endnoteSettings, Properties = ReadDocumentProperties(customPropertiesXml),
            CustomProperties = ReadTypedProperties(customPropertiesXml), CoreProperties = ReadCoreProperties(Xml("docProps/core.xml")),
            CustomXmlParts = ReadCustomXmlParts(relationships.Values, path => Xml(path)),
            CompatibilitySettings = new DocumentCompatibilitySettings { Xml = settings?.Root?.Element(W + "compat")?.ToString(SaveOptions.DisableFormatting) },
            Defaults = new DocumentDefaults { Text = defaultText, Paragraph = defaultParagraph } };
        foreach (var property in body.Descendants().Concat(styleRoot?.Descendants() ?? []))
        {
            foreach (var attribute in property.Attributes().Where(a => a.Name.LocalName is "themeColor" or "themeFill"))
                if (!theme.Colors.ContainsKey(attribute.Value)) Loss("theme-color", "Unresolved theme color reference", "Reference retained with explicit fallback color.", property);
            foreach (var attribute in property.Attributes().Where(a => a.Name.LocalName is "asciiTheme" or "hAnsiTheme" or "eastAsiaTheme" or "cstheme"))
                if (!theme.Fonts.ContainsKey(attribute.Value)) Loss("theme-font", "Unresolved theme font reference", "Reference retained with explicit fallback font.", property);
        }
        foreach (var unfinished in bookmarkStarts.Values) Loss("bookmark", "Unclosed bookmark", "Omitted the detached bookmark.", id: unfinished.Id);
        if (permissionStarts.Count != 0 || controlStarts.Count != 0) throw new FormatException("Unclosed form or permission boundary.");
        var protection = ReadProtection(settings?.Root);
        var sectionProperties = body.Descendants(W + "sectPr").Where(p => !p.Ancestors(W + "tbl").Any()).ToArray();
        if (settings?.Root?.Element(W + "documentProtection")?.Attribute(Tx + "protectedSections") is { } protectedSections)
        {
            var selectedSections = protectedSections.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (selectedSections.Any(index => index < 0 || index >= document.Sections.Length)) throw new FormatException("Invalid protected section index.");
            protection = protection with { ProtectedSectionIds = selectedSections.Select(index => document.Sections[index].Id).ToImmutableArray() };
        }
        else if (sectionProperties.Any(p => p.Element(W + "formProt") is not null))
            protection = protection with { ProtectedSectionIds = document.Sections.Where((section, index) => index < sectionProperties.Length && On(sectionProperties[index].Element(W + "formProt"))).Select(section => section.Id).ToImmutableArray() };
        else protection = protection with { ProtectedSectionIds = [] };
        document = RangeInterchange.ResolveStories(document with { Bookmarks = bookmarks.ToImmutableArray(), Fields = generalFields.ToImmutableArray(),
            ContentControls = contentControls.ToImmutableArray(), PermissionRanges = permissionRanges.ToImmutableArray(), Protection = protection });
        document = ContentControlValidation.SynchronizeValues(document);
        document = document.PruneUnusedResources();
        document.Validate();
        return document;
    }
    private static Dictionary<string, NumberingInfo> ReadNumbering(XElement? root)
    {
        var result = new Dictionary<string, NumberingInfo>();
        if (root is null) return result;
        foreach (var num in root.Elements(W + "num"))
        {
            var abstractId = Value(num.Element(W + "abstractNumId"));
            var definition = root.Elements(W + "abstractNum").FirstOrDefault(e => (string?)e.Attribute(W + "abstractNumId") == abstractId);
            if (definition is null) { Loss("numbering-missing", "Missing numbering definition", "Decimal markers applied.", num); continue; }
            var levels = Enumerable.Range(0, 9).Select(_ => new ListLevelDefinition()).ToArray();
            foreach (var level in definition.Elements(W + "lvl"))
            {
                var index = (int?)level.Attribute(W + "ilvl") ?? 0;
                if (index is < 0 or > 8) throw new FormatException("DOCX numbering level exceeds the model limit.");
                levels[index] = ReadListLevel(level, index);
                if (level.Element(W + "rPr") is not null || level.Element(W + "pPr") is not null)
                    Loss("numbering-formatting", "Marker-specific font or indentation", "Marker text and paragraph direct formatting retained.", level);
            }
            var starts = new Dictionary<int, int>();
            foreach (var levelOverride in num.Elements(W + "lvlOverride"))
            {
                var index = (int?)levelOverride.Attribute(W + "ilvl") ?? 0;
                if (index is < 0 or > 8) throw new FormatException("Invalid DOCX numbering override level.");
                if (levelOverride.Element(W + "lvl") is { } level) levels[index] = ReadListLevel(level, index);
                if (levelOverride.Element(W + "startOverride") is { } start)
                {
                    var value = Number(start, 1);
                    if (value is < 1 or > 1000000) throw new FormatException("DOCX list start exceeds model limits.");
                    starts[index] = value;
                }
            }
            var name = Value(definition.Element(W + "name"));
            var identity = name?.StartsWith(ListNamePrefix, StringComparison.Ordinal) == true && Guid.TryParse(name[ListNamePrefix.Length..], out var id) && id != Guid.Empty ? id : Guid.NewGuid();
            result.Add((string?)num.Attribute(W + "numId") ?? throw new FormatException("Missing DOCX numbering identity."), new(identity, new ListDefinition { Levels = levels.ToImmutableArray() }, starts));
        }
        return result;
    }
    private static ListLevelDefinition ReadListLevel(XElement level, int index)
    {
        var format = Value(level.Element(W + "numFmt"));
        var marker = format switch { "bullet" => ListMarkerStyle.Bullet, "lowerLetter" => ListMarkerStyle.LowerLetter, "upperLetter" => ListMarkerStyle.UpperLetter,
            "lowerRoman" => ListMarkerStyle.LowerRoman, "upperRoman" => ListMarkerStyle.UpperRoman, _ => ListMarkerStyle.Decimal };
        if (format is not (null or "decimal" or "bullet" or "lowerLetter" or "upperLetter" or "lowerRoman" or "upperRoman")) Loss("number-format", "Unsupported numbering format " + format, "Decimal markers applied.", level);
        var text = Value(level.Element(W + "lvlText")) ?? (marker == ListMarkerStyle.Bullet ? "â€¢" : $"%{index + 1}.");
        var start = Number(level.Element(W + "start"), 1);
        if (start is < 1 or > 1000000) throw new FormatException("DOCX list start exceeds model limits.");
        var own = $"%{index + 1}";
        var ancestors = string.Join(".", Enumerable.Range(1, index + 1).Select(i => $"%{i}"));
        var pattern = text.Contains(ancestors, StringComparison.Ordinal) ? ancestors : own;
        var position = text.IndexOf(pattern, StringComparison.Ordinal);
        var prefix = position < 0 ? "" : text[..position]; var suffix = position < 0 ? "." : text[(position + pattern.Length)..];
        if (marker != ListMarkerStyle.Bullet && (position < 0 || prefix.Contains('%') || suffix.Contains('%')))
        { Loss("numbering-pattern", "Unsupported composite numbering pattern", "Current-level marker retained.", level); prefix = ""; suffix = "."; }
        if (text.Length > 100 || prefix.Length > 100 || suffix.Length > 100) throw new FormatException("DOCX list marker exceeds model limits.");
        if (level.Element(W + "lvlRestart") is { } restart && Number(restart) != index) Loss("numbering-restart-rule", "Custom numbering restart rule", "Deeper levels restart after their parent changes.", restart);
        return new() { Kind = marker == ListMarkerStyle.Bullet ? ListKind.Bullet : ListKind.Numbered, Marker = marker, Text = marker == ListMarkerStyle.Bullet ? text : null,
            Start = start, Prefix = prefix, Suffix = suffix, IncludeAncestors = index > 0 && pattern == ancestors,
            MarkerFormatting = ReadTextOverrides(level.Element(W + "rPr")),
            CharacterStyleId = Value(level.Element(W + "rPr")?.Element(W + "rStyle")),
            ParagraphStyleId = Value(level.Element(W + "pStyle")),
            TextIndent = ListDimension(level.Element(W + "pPr")?.Element(W + "ind"), "left"),
            MarkerIndent = ListMarkerIndent(level.Element(W + "pPr")?.Element(W + "ind")),
            TabPosition = ListDimension(level.Element(W + "pPr")?.Element(W + "tabs")?.Elements(W + "tab").FirstOrDefault(e => (string?)e.Attribute(W + "val") == "num"), "pos"),
            FollowCharacter = Value(level.Element(W + "suff")) switch { "space" => ListFollowCharacter.Space, "nothing" => ListFollowCharacter.Nothing, _ => ListFollowCharacter.Tab } };
        static double? ListDimension(XElement? element, string name) => element?.Attribute(W + name) is { } attribute
            ? double.Parse(attribute.Value, CultureInfo.InvariantCulture) / 15 : null;
        static double? ListMarkerIndent(XElement? indent)
        {
            var left = ListDimension(indent, "left");
            return left is null ? null : Math.Max(0, left.Value - (ListDimension(indent, "hanging") ?? 0) + (ListDimension(indent, "firstLine") ?? 0));
        }
    }
    private static string? Value(XElement? element) => (string?)element?.Attribute(W + "val");
    private static int Number(XElement? element, int fallback = 0) => Value(element) is not { } value ? fallback :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new FormatException("Invalid DOCX integer value.");
    private static double Dimension(XElement? element, XName attribute, double fallback = 0) => element?.Attribute(attribute) is not { } value ? fallback :
        double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : throw new FormatException("Invalid DOCX dimension.");
    private static double Bounded(double value, double min, double max, XElement? source)
    {
        if (value < min || value > max) Loss("dimension-range", "Formatting outside flow-model limits", "Value limited to the supported range.", source);
        return Math.Clamp(value, min, max);
    }
    private static bool On(XElement? element) => element is not null && Value(element) is not ("false" or "0" or "off" or "none");
    private static bool OnAttribute(XElement element, string name) => (string?)element.Attribute(W + name) is "true" or "1" or "on";
    private static string? ReadColor(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-fA-F]{6}$") ? "#" + value : null;
    private static string? ResolvePart(string target, string sourcePart = "word/document.xml")
    {
        if (target.Contains('\\') || target.Contains(':') || target.Contains('?') || target.Contains('#')) return null;
        var parts = new List<string>();
        foreach (var segment in (target.StartsWith('/') ? target[1..] : sourcePart[..(sourcePart.LastIndexOf('/') + 1)] + target).Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); } else parts.Add(segment);
        }
        return string.Join('/', parts);
    }
    private static string? ImageExtension(string? type) => type?.ToLowerInvariant() switch { "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif", "image/bmp" => "bmp", "image/tiff" => "tif", "image/svg+xml" => "svg", "image/x-emf" or "image/emf" => "emf", "image/x-wmf" or "image/wmf" => "wmf", "image/webp" => "webp", "image/x-icon" or "image/vnd.microsoft.icon" => "ico", _ => null };
    private static string? ImageMediaType(string extension) => extension.ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".svg" => "image/svg+xml", ".emf" => "image/x-emf", ".wmf" => "image/x-wmf", ".webp" => "image/webp", ".ico" => "image/x-icon", _ => null };
    private static EdgeInsets? ReadPadding(XElement? properties)
    {
        if (properties is null) return null;
        double Side(string name)
        {
            var side = properties.Element(W + name) ?? properties.Element(W + (name == "left" ? "start" : name == "right" ? "end" : name));
            if ((string?)side?.Attribute(W + "type") is not (null or "dxa"))
            { Loss("padding-unit", "Non-twip cell padding", "Unsupported padding side set to zero.", side); return 0; }
            return Bounded(Dimension(side, W + "w") / 15, 0, 1000, side);
        }
        return new(Side("left"), Side("top"), Side("right"), Side("bottom"));
    }
    private static BorderSide? ReadBorder(XElement? property)
    {
        if (property is null) return null;
        var type = Value(property);
        if (type is "nil" or "none") return new() { Kind = BorderKind.None };
        if (type is not ("single" or "dashed" or "dotted" or "double" or null)) Loss("border-style", "Unsupported border style: " + type, "Solid border retained.", property);
        return new(Bounded(Dimension(property, W + "sz", 4) / 6, 0, 1000, property), ReadColor((string?)property.Attribute(W + "color")))
        { Kind = type switch { "dashed" => BorderKind.Dashed, "dotted" => BorderKind.Dotted, "double" => BorderKind.Double, _ => BorderKind.Solid } };
    }
    private static BlockBorders? ReadBorders(XElement? properties) => properties is null ? null : new(ReadBorder(properties.Element(W + "left")), ReadBorder(properties.Element(W + "top")), ReadBorder(properties.Element(W + "right")), ReadBorder(properties.Element(W + "bottom")));
    private static TextStyle ReadTextStyle(XElement? properties, TextStyle style)
    {
        if (properties is null) return style;
        foreach (var property in properties.Elements())
            switch (property.Name.LocalName)
            {
                case "b": style = style with { Bold = On(property), FontWeight = null }; break;
                case "i": style = style with { Italic = On(property) }; break;
                case "u":
                    var underline = Value(property) switch { "none" => UnderlineKind.None, "double" => UnderlineKind.Double, "dotted" => UnderlineKind.Dotted,
                        "dash" => UnderlineKind.Dashed, "thick" => UnderlineKind.Thick, "wave" => UnderlineKind.Wave, _ => UnderlineKind.Single };
                    style = style with { Underline = underline != UnderlineKind.None, UnderlineKind = underline, UnderlineWordsOnly = Value(property) == "words", UnderlineColor = ReadColor((string?)property.Attribute(W + "color")) };
                    if (Value(property) is not (null or "none" or "single" or "double" or "dotted" or "dash" or "thick" or "wave" or "words")) Loss("underline-style", "Unsupported underline pattern", "Single underline retained.", property);
                    break;
                case "strike": style = style with { Strikethrough = On(property), StrikeKind = On(property) ? StrikeKind.Single : StrikeKind.None }; break;
                case "dstrike": if (On(property)) style = style with { Strikethrough = true, StrikeKind = StrikeKind.Double };
                    else if (style.StrikeKind == StrikeKind.Double) style = style with { Strikethrough = false, StrikeKind = StrikeKind.None }; break;
                case "sz": style = style with { FontSize = Bounded(Number(property, 24) * 2d / 3, 1, 512, property) }; break;
                case "rFonts":
                    style = style with { FontFamily = (string?)property.Attribute(W + "ascii") ?? (string?)property.Attribute(W + "hAnsi") ?? style.FontFamily,
                        EastAsianFontFamily = (string?)property.Attribute(W + "eastAsia") ?? style.EastAsianFontFamily,
                        ComplexScriptFontFamily = (string?)property.Attribute(W + "cs") ?? style.ComplexScriptFontFamily,
                        ThemeFont = ThemeFont(property, "asciiTheme") ?? ThemeFont(property, "hAnsiTheme") ?? style.ThemeFont,
                        EastAsianThemeFont = ThemeFont(property, "eastAsiaTheme") ?? style.EastAsianThemeFont, ComplexScriptThemeFont = ThemeFont(property, "cstheme") ?? style.ComplexScriptThemeFont };
                    break;
                case "color":
                    style = style with { Foreground = ReadColor(Value(property)), ThemeForeground = ThemeColor(property, "themeColor", "themeTint", "themeShade") }; break;
                case "shd":
                    style = style with { Background = ReadColor((string?)property.Attribute(W + "fill")), ThemeBackground = ThemeColor(property, "themeFill", "themeFillTint", "themeFillShade") }; break;
                case "highlight": if (Avalonia.Media.Color.TryParse(Value(property), out var color)) style = style with { Background = $"#{color.R:X2}{color.G:X2}{color.B:X2}" }; break;
                case "vertAlign": style = style with { Baseline = Value(property) switch { "subscript" => Baseline.Subscript, "superscript" => Baseline.Superscript, _ => Baseline.Normal } }; break;
                case "caps": style = style with { AllCaps = On(property) }; break;
                case "smallCaps": style = style with { SmallCaps = On(property) }; break;
                case "lang": style = style with { Language = Value(property) }; break;
                case "noProof": style = style with { NoProof = On(property) }; break;
                case "spacing": style = style with { Tracking = Bounded(Number(property) / 15d, -1000, 1000, property) }; break;
                case "w": style = style with { HorizontalScale = Bounded(Number(property, 100) / 100d, .01, 6, property) }; break;
                case "position": style = style with { BaselineOffset = Bounded(Number(property) * 2d / 3, -1000, 1000, property) }; break;
                case "kern": style = style with { KerningThreshold = Bounded(Number(property) * 2d / 3, 0, 512, property) }; break;
                case "rStyle": case "rtl": case "cs": case "bCs": case "iCs": case "szCs": break;
                default: Loss("text-property", property.Name.LocalName, "Unsupported text formatting omitted.", property); break;
            }
        return style;
    }
    private static ParagraphStyle ReadParagraphStyle(XElement? properties, ParagraphStyle style, Dictionary<string, NumberingInfo> numbering)
    {
        if (properties is null) return style;
        if (properties.Attribute(Tx + "hyphenateCaps") is { } caps)
            style = style with { HyphenateCaps = caps.Value is "1" or "true" };
        foreach (var p in properties.Elements())
            switch (p.Name.LocalName)
            {
                case "jc": style = style with { Alignment = Value(p) switch { "center" => ParagraphAlignment.Center, "right" or "end" => ParagraphAlignment.Right, "both" or "distribute" => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left } }; break;
                case "bidi": style = style with { RightToLeft = On(p) }; break;
                case "ind": style = style with
                    {
                        Indent = Bounded(Dimension(p, W + "left", style.Indent * 15) / 15, 0, 1000, p), RightIndent = Bounded(Dimension(p, W + "right", style.RightIndent * 15) / 15, 0, 100000, p),
                        FirstLineIndent = Bounded(p.Attribute(W + "hanging") is not null ? -Dimension(p, W + "hanging") / 15 : Dimension(p, W + "firstLine", style.FirstLineIndent * 15) / 15, -100000, 100000, p)
                    }; break;
                case "spacing":
                    style = style with { SpaceBefore = Bounded(Dimension(p, W + "before", style.SpaceBefore * 15) / 15, 0, 1000, p), SpaceAfter = Bounded(Dimension(p, W + "after", style.SpaceAfter * 15) / 15, 0, 1000, p) };
                    if (p.Attribute(W + "line") is not null)
                    {
                        var mode = (string?)p.Attribute(W + "lineRule") switch { "exact" => LineSpacingMode.Exact, "atLeast" => LineSpacingMode.AtLeast, _ => LineSpacingMode.Multiple };
                        var line = Bounded(Dimension(p, W + "line", 240) / (mode == LineSpacingMode.Multiple ? 240 : 15), .01, 10000, p);
                        style = style with { LineSpacingMode = mode, LineSpacing = line, LineHeight = mode == LineSpacingMode.Exact ? line : null };
                    }
                    break;
                case "numPr":
                    var numId = Value(p.Element(W + "numId")); var level = Number(p.Element(W + "ilvl"), style.ListLevel);
                    if (level is < 0 or > 8) throw new FormatException("Invalid DOCX list level.");
                    if (numId == "0") style = style with { List = ListKind.None, ListId = null, ListDefinition = null, ListLevel = 0, ListStart = null, ListRestart = false };
                    else if (numId is not null && numbering.TryGetValue(numId, out var info)) style = style with { List = info.Definition.Levels[level].Kind, ListLevel = level, ListId = info.Identity, ListDefinition = info.Definition };
                    else if (numId is null) style = style with { ListLevel = level };
                    else { style = style with { List = ListKind.Numbered, ListLevel = level }; Loss("numbering-missing", "Missing numbering instance", "Decimal markers applied.", p); }
                    break;
                case "outlineLvl": style = style with { HeadingLevel = Number(p) is >= 0 and < 6 ? Number(p) + 1 : 0, OutlineLevel = Number(p) is >= 0 and < 9 ? Number(p) + 1 : 0 }; break;
                case "tabs":
                    var tabs = style.TabStops.ToDictionary(t => t.Position);
                    foreach (var tab in p.Elements(W + "tab"))
                    {
                        var position = Bounded(Dimension(tab, W + "pos") / 15, 0, 100000, tab);
                        if (Value(tab) == "clear") { tabs.Remove(position); continue; }
                        if (Value(tab) is "bar" or "num") { Loss("tab-alignment", "Unsupported tab alignment", "Left-aligned tab retained.", tab); }
                        tabs[position] = new TabStop(position, Value(tab) switch { "center" => TabAlignment.Center, "right" or "end" => TabAlignment.Right, "decimal" => TabAlignment.Decimal, _ => TabAlignment.Left },
                            (string?)tab.Attribute(W + "leader") switch { "dot" => TabLeader.Dots, "hyphen" => TabLeader.Dashes, "underscore" => TabLeader.Line, _ => TabLeader.None });
                    }
                    style = style with { TabStops = tabs.Values.OrderBy(t => t.Position).ToImmutableArray() }; break;
                case "contextualSpacing": style = style with { ContextualSpacing = On(p) }; break;
                case "suppressAutoHyphens": style = style with { SuppressHyphenation = On(p) }; break;
                case "pBdr": style = style with { Borders = ReadBorders(p) }; break;
                case "shd": style = style with { Shading = ReadColor((string?)p.Attribute(W + "fill")) }; break;
                case "pageBreakBefore": style = style with { PageBreakBefore = On(p) }; break;
                case "keepNext": style = style with { KeepWithNext = On(p) }; break;
                case "keepLines": style = style with { KeepTogether = On(p) }; break;
                case "widowControl": style = style with { WidowControl = On(p) }; break;
                case "snapToGrid": style = style with { SnapToGrid = On(p) }; break;
                case "pStyle": case "rPr": case "sectPr": break;
                default: Loss("paragraph-property", p.Name.LocalName, "Unsupported paragraph formatting omitted.", p); break;
            }
        return style;
    }
    private static XElement Val(string name, object value) => new(W + name, new XAttribute(W + "val", value));
    private static int Twips(double value)
    {
        if (Math.Abs(value * 15 - Math.Round(value * 15)) > .000001) Loss("dimension-precision", "Sub-twip geometry", "Dimension rounded to the nearest twip.");
        return (int)Math.Round(value * 15);
    }
    private static string Color(string value, Guid id)
    {
        if (value.Length == 9 && !value.StartsWith("#FF", StringComparison.OrdinalIgnoreCase)) Loss("color-alpha", "Color transparency", "Opaque RGB color retained.", id: id);
        return value[^6..];
    }
    private static XElement WriteTextStyle(TextStyle source, Guid id, double letterSpacing = 0)
    {
        var o = source.Overrides;
        var style = o?.Apply(TextStyle.Default) ?? source;
        bool Has(bool specified) => o is null || specified;
        if (style.FontWeight is { } weight && weight is not (400 or 700)) Loss("font-weight", "Numeric font weight", "Nearest regular/bold weight retained.", id: id);
        if (style.FontStretch != 5) Loss("font-stretch", "Font stretch", "Normal font stretch retained.", id: id);
        if (Math.Abs(style.FontSize * 1.5 - Math.Round(style.FontSize * 1.5)) > .000001) Loss("font-size", "Fractional half-point font size", "Font size rounded to the nearest half point.", id: id);
        if (Math.Abs(style.HorizontalScale * 100 - Math.Round(style.HorizontalScale * 100)) > .000001 ||
            Math.Abs(style.BaselineOffset * 1.5 - Math.Round(style.BaselineOffset * 1.5)) > .000001 ||
            style.KerningThreshold is { } kern && Math.Abs(kern * 1.5 - Math.Round(kern * 1.5)) > .000001)
            Loss("typography-precision", "Run typography outside DOCX numeric precision", "Horizontal scale rounded to whole percent; position and kerning rounded to half points.", id: id);
        if (o?.FontFamily.IsSet == true && style.FontFamily is null && style.ThemeFont is null ||
            o?.EastAsianFontFamily.IsSet == true && style.EastAsianFontFamily is null && style.EastAsianThemeFont is null ||
            o?.ComplexScriptFontFamily.IsSet == true && style.ComplexScriptFontFamily is null && style.ComplexScriptThemeFont is null)
            Loss("font-inheritance-reset", "Explicit inherited font reset", "Parent or document font retained; DOCX requires a concrete font name or theme reference.", id: id);
        if (o?.KerningThreshold.IsSet == true && style.KerningThreshold is null)
            Loss("kerning-reset", "Explicit automatic kerning reset", "Inherited kerning setting retained.", id: id);
        var fonts = new XElement(W + "rFonts",
            style.FontFamily is { } family ? new[] { new XAttribute(W + "ascii", family), new XAttribute(W + "hAnsi", family) } : null,
            style.EastAsianFontFamily is { } east ? new XAttribute(W + "eastAsia", east) : null,
            style.ComplexScriptFontFamily is { } complex ? new XAttribute(W + "cs", complex) : null,
            style.ThemeFont is { } font ? new[] { new XAttribute(W + "asciiTheme", font.Name), new XAttribute(W + "hAnsiTheme", font.Name) } : null,
            style.EastAsianThemeFont is { } eastTheme ? new XAttribute(W + "eastAsiaTheme", eastTheme.Name) : null,
            style.ComplexScriptThemeFont is { } complexTheme ? new XAttribute(W + "cstheme", complexTheme.Name) : null);
        var underline = style.UnderlineKind != UnderlineKind.None ? style.UnderlineKind : style.Underline ? UnderlineKind.Single : UnderlineKind.None;
        var strike = style.StrikeKind != StrikeKind.None ? style.StrikeKind : style.Strikethrough ? StrikeKind.Single : StrikeKind.None;
        return new XElement(W + "rPr",
            source.StyleId is { } styleId ? Val("rStyle", styleId) : null,
            fonts.HasAttributes ? fonts : null,
            Has(o?.Bold.IsSet == true || o?.FontWeight.IsSet == true) ? Val("b", style.EffectiveBold ? 1 : 0) : null,
            Has(o?.Italic.IsSet == true) ? Val("i", style.Italic ? 1 : 0) : null,
            Has(o?.Strikethrough.IsSet == true || o?.StrikeKind.IsSet == true) ? new[] { Val("strike", strike == StrikeKind.Single ? 1 : 0), Val("dstrike", strike == StrikeKind.Double ? 1 : 0) } : null,
            Has(o?.Foreground.IsSet == true || o?.ThemeForeground.IsSet == true) ? ColorElement("color", "val", style.Foreground, style.ThemeForeground, id) ?? (o is not null ? Val("color", "auto") : null) : null,
            Has(o?.Background.IsSet == true || o?.ThemeBackground.IsSet == true) ? ColorElement("shd", "fill", style.Background, style.ThemeBackground, id) ?? (o is not null ? new XElement(W + "shd", new XAttribute(W + "fill", "auto"), new XAttribute(W + "val", "clear")) : null) : null,
            Has(o?.Tracking.IsSet == true) && (style.Tracking != 0 || letterSpacing != 0 || o?.Tracking.IsSet == true) ? Val("spacing", Twips(style.Tracking + letterSpacing)) : null,
            Has(o?.FontSize.IsSet == true) ? Val("sz", (int)Math.Round(style.FontSize * 1.5)) : null,
            Has(o?.Underline.IsSet == true || o?.UnderlineKind.IsSet == true || o?.UnderlineWordsOnly.IsSet == true || o?.UnderlineColor.IsSet == true) ?
                new XElement(W + "u", new XAttribute(W + "val", style.UnderlineWordsOnly && underline != UnderlineKind.None ? "words" : underline switch { UnderlineKind.Single => "single", UnderlineKind.Double => "double", UnderlineKind.Dotted => "dotted", UnderlineKind.Dashed => "dash", UnderlineKind.Thick => "thick", UnderlineKind.Wave => "wave", _ => "none" }), style.UnderlineColor is { } underlineColor ? new XAttribute(W + "color", Color(underlineColor, id)) : null) : null,
            Has(o?.Baseline.IsSet == true) ? Val("vertAlign", style.Baseline switch { Baseline.Subscript => "subscript", Baseline.Superscript => "superscript", _ => "baseline" }) : null,
            Has(o?.AllCaps.IsSet == true) && (style.AllCaps || o is not null) ? Val("caps", style.AllCaps ? 1 : 0) : null,
            Has(o?.SmallCaps.IsSet == true) && (style.SmallCaps || o is not null) ? Val("smallCaps", style.SmallCaps ? 1 : 0) : null,
            Has(o?.NoProof.IsSet == true) && (style.NoProof || o is not null) ? Val("noProof", style.NoProof ? 1 : 0) : null,
            style.Language is { } language ? Val("lang", language) : null,
            Has(o?.HorizontalScale.IsSet == true) && (style.HorizontalScale != 1 || o is not null) ? Val("w", (int)Math.Round(style.HorizontalScale * 100)) : null,
            Has(o?.BaselineOffset.IsSet == true) && (style.BaselineOffset != 0 || o is not null) ? Val("position", (int)Math.Round(style.BaselineOffset * 1.5)) : null,
            style.KerningThreshold is { } threshold ? Val("kern", (int)Math.Round(threshold * 1.5)) : null);
    }
    private static XElement? ColorElement(string element, string valueAttribute, string? color, ThemeColorReference? reference, Guid id)
    {
        if (color is null && reference is null) return null;
        var shading = element == "shd";
        return new XElement(W + element, new XAttribute(W + valueAttribute, color is null ? "auto" : Color(color, id)),
            shading ? new XAttribute(W + "val", "clear") : null,
            reference is null ? null : new XAttribute(W + (shading ? "themeFill" : "themeColor"), reference.Name),
            reference is not null && reference.Tint != 0 ? new XAttribute(W + (shading ? "themeFill" : "theme") + (reference.Tint > 0 ? "Tint" : "Shade"), ((int)Math.Round((1 - Math.Abs(reference.Tint)) * 255)).ToString("X2", CultureInfo.InvariantCulture)) : null);
    }
    private static XElement WritePadding(string name, EdgeInsets edges) => new(W + name,
        new[] { ("top", edges.Top), ("left", edges.Left), ("bottom", edges.Bottom), ("right", edges.Right) }.Select(s =>
            new XElement(W + s.Item1, new XAttribute(W + "w", Twips(s.Item2)), new XAttribute(W + "type", "dxa"))));
    private static int BorderUnits(double width, Guid id)
    {
        var units = width * 6;
        var rounded = Math.Clamp(Math.Round(units), 2, 96);
        if (Math.Abs(units - rounded) > .000001) Loss("dimension-precision", "Border width outside DOCX precision or range", "Border rounded to an eighth point and limited to 2â€“96 eighth points.", id: id);
        return (int)rounded;
    }
    private static XElement WriteBorders(BlockBorders borders, Guid id) => new(W + "tcBorders",
        new[] { ("top", borders.Top), ("left", borders.Left), ("bottom", borders.Bottom), ("right", borders.Right) }.Select(s => WriteBorder(s.Item1, s.Item2, id)));
    private static byte[] Write(FlowDocument document, CancellationToken token, bool template)
    {
        document = MapStyleIdentifiers(document);
        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            void Part(string name, XElement element)
            {
                WrapContentControls(element);
                if (element.DescendantsAndSelf().Any(e => e.Name.Namespace == Tx || e.Attributes().Any(a => a.Name.Namespace == Tx)))
                {
                    XNamespace compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
                    element.SetAttributeValue(XNamespace.Xmlns + "tx", Tx.NamespaceName);
                    element.SetAttributeValue(XNamespace.Xmlns + "mc", compatibility.NamespaceName);
                    element.SetAttributeValue(compatibility + "Ignorable", "tx");
                }
                var extensionNamespaces = new[] { (Prefix: "w14", Namespace: W14), (Prefix: "w15", Namespace: W15) }.Where(pair => element.DescendantsAndSelf().Any(e => e.Name.Namespace == pair.Namespace)).ToArray();
                if (extensionNamespaces.Length != 0)
                {
                    XNamespace compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
                    element.SetAttributeValue(XNamespace.Xmlns + "mc", compatibility.NamespaceName);
                    foreach (var pair in extensionNamespaces) element.SetAttributeValue(XNamespace.Xmlns + pair.Prefix, pair.Namespace.NamespaceName);
                    element.SetAttributeValue(compatibility + "Ignorable", string.Join(" ", new[] { (string?)element.Attribute(compatibility + "Ignorable") }.Where(v => v is not null).Concat(extensionNamespaces.Select(p => p.Prefix))));
                }
                token.ThrowIfCancellationRequested();
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                new XDocument(new XDeclaration("1.0", "utf-8", "yes"), element).Save(stream);
            }
            void RawPart(string name, string xml)
            {
                token.ThrowIfCancellationRequested();
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
                XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Save(writer);
            }
            var relationships = new List<XElement>
            {
                new(Rel + "Relationship", new XAttribute("Id", "styles"), new XAttribute("Type", R.NamespaceName + "/styles"), new XAttribute("Target", "styles.xml")),
                new(Rel + "Relationship", new XAttribute("Id", "numbering"), new XAttribute("Type", R.NamespaceName + "/numbering"), new XAttribute("Target", "numbering.xml"))
            };
            var noteNumbers = document.Notes.Select((note, index) => (note.Id, Number: index + 1)).ToDictionary(n => n.Id, n => n.Number);
            var writtenParagraphs = new Dictionary<Guid, XElement>();
            var permissionIds = document.PermissionRanges.Select((permission, index) => (permission.Id, index)).ToDictionary(p => p.Id, p => p.index);
            var bookmarkIds = document.Bookmarks.Select((bookmark, index) => (bookmark.Id, index)).ToDictionary(p => p.Id, p => p.index);
            var storyContentTypes = new List<XElement>();
            var resolver = new DocumentStyleResolver(document);
            var numbering = new XElement(W + "numbering"); var numbers = new List<XElement>();
            var identities = new Dictionary<Guid, (int Abstract, int Number, ListDefinition Definition, ListKind Kind)>();
            var resourceIds = new Dictionary<(string Id, string Kind), string>(); var mediaTypes = new Dictionary<string, string>();
            var nextNumber = 0; var nextAbstract = 0; var drawingId = 0;
            int NumberFor(Paragraph paragraph, Guid anonymousIdentity)
            {
                var ps = paragraph.Style; var identity = ps.ListId ?? anonymousIdentity;
                var exists = identities.TryGetValue(identity, out var current);
                var definition = ps.ListDefinition ?? (exists ? current.Definition : new ListDefinition());
                var changedDefinition = exists && (!current.Definition.Equals(definition) || current.Kind != ps.List);
                if (definition.Level(ps.ListLevel, ps.List).Kind != ps.List)
                    Loss("list-kind", "Paragraph list kind differs from its level definition", "Level definition marker kind retained.", id: paragraph.Id);
                if (!exists || changedDefinition)
                {
                    var abstractId = ++nextAbstract;
                    numbering.Add(new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", abstractId),
                        Val("multiLevelType", "multilevel"), Val("name", ListNamePrefix + identity),
                        Enumerable.Range(0, 9).Select(level =>
                        {
                            var format = definition.Level(level, ps.List);
                            var marker = format.Kind == ListKind.Bullet ? ListMarkerStyle.Bullet : format.Marker;
                            var text = marker == ListMarkerStyle.Bullet ? format.Text ?? "â€¢" : format.Prefix +
                                (format.IncludeAncestors ? string.Join(".", Enumerable.Range(1, level + 1).Select(i => $"%{i}")) : $"%{level + 1}") + format.Suffix;
                            return new XElement(W + "lvl", new XAttribute(W + "ilvl", level), Val("start", format.Start),
                                Val("numFmt", marker switch { ListMarkerStyle.Bullet => "bullet", ListMarkerStyle.LowerLetter => "lowerLetter", ListMarkerStyle.UpperLetter => "upperLetter", ListMarkerStyle.LowerRoman => "lowerRoman", ListMarkerStyle.UpperRoman => "upperRoman", _ => "decimal" }),
                                format.ParagraphStyleId is { } linked ? Val("pStyle", linked) : null,
                                Val("suff", format.FollowCharacter switch { ListFollowCharacter.Space => "space", ListFollowCharacter.Nothing => "nothing", _ => "tab" }), Val("lvlText", text),
                                new XElement(W + "pPr",
                                    format.TextIndent is not null || format.MarkerIndent is not null ? new XElement(W + "ind",
                                        new XAttribute(W + "left", Twips(format.TextIndent ?? 28 + level * 24)),
                                        new XAttribute(W + ((format.TextIndent ?? 28 + level * 24) >= (format.MarkerIndent ?? 4 + level * 24) ? "hanging" : "firstLine"),
                                            Twips(Math.Abs((format.TextIndent ?? 28 + level * 24) - (format.MarkerIndent ?? 4 + level * 24))))) : null,
                                    format.TabPosition is { } tab ? new XElement(W + "tabs", new XElement(W + "tab", new XAttribute(W + "val", "num"), new XAttribute(W + "pos", Twips(tab)))) : null),
                                WriteTextStyle(TextStyle.ForStyle(format.CharacterStyleId) with { Overrides = format.MarkerFormatting }, paragraph.Id));
                        })));
                    current = (abstractId, 0, definition, ps.List);
                }
                if (current.Number == 0 || ps.ListRestart || ps.ListStart is not null)
                {
                    var number = ++nextNumber;
                    var num = new XElement(W + "num", new XAttribute(W + "numId", number), Val("abstractNumId", current.Abstract));
                    if (ps.ListRestart || ps.ListStart is not null || changedDefinition)
                    {
                        var start = ps.ListStart ?? (changedDefinition ? ListNumbering.GetMarker(document, paragraph.Id)!.Number : definition.Level(ps.ListLevel, ps.List).Start);
                        num.Add(new XElement(W + "lvlOverride", new XAttribute(W + "ilvl", ps.ListLevel), Val("startOverride", start)));
                        if (ps.ListLevel > 0) Loss("nested-list-restart", "Nested list restart ancestor counters", "Restart value retained; Word may reset ancestor counters for the new numbering instance.", id: paragraph.Id);
                    }
                    numbers.Add(num); current = (current.Abstract, number, definition, ps.List);
                }
                identities[identity] = current;
                return current.Number;
            }
            bool HasResource(string resourceId, bool image) => document.Resources.TryGetValue(resourceId, out var resource) &&
                resource.Kind == DocumentResourceKind.Embedded && (!image || ImageExtension(resource.MediaType) is not null);
            string? WriteResource(string resourceId, bool image, string oleKind = "oleObject")
            {
                if (!HasResource(resourceId, image)) return null;
                var resource = document.Resources[resourceId];
                var kind = image ? "image" : oleKind;
                if (resourceIds.TryGetValue((resourceId, kind), out var existing)) return existing;
                var extension = image ? ImageExtension(resource.MediaType)! : "bin";
                var relationshipId = kind + resourceIds.Count;
                var name = (image ? "media/" : "embeddings/") + relationshipId + "." + extension;
                using (var target = archive.CreateEntry("word/" + name, CompressionLevel.Optimal).Open()) target.Write(resource.Data.AsSpan());
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", relationshipId), new XAttribute("Type", R.NamespaceName + "/" + kind), new XAttribute("Target", name)));
                resourceIds[(resourceId, kind)] = relationshipId;
                storyContentTypes.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/word/" + name), new XAttribute("ContentType", resource.MediaType)));
                return relationshipId;
            }
            XElement? WriteImage(InlineDescriptor inline)
            {
                if (inline.Payload is OleInlinePayload ole)
                {
                    if (inline.Placement is not null) Loss("ole-placement", "OLE preview placement, crop and rotation", "Placement retained in a Textalonia extension; other editors show an inline untransformed preview.", id: inline.Id);
                    if (HasResource(ole.ResourceId, false) && HasResource(ole.PreviewResourceId, true) &&
                        WriteResource(ole.ResourceId, false, ole.RelationshipKind == OleRelationshipKind.Package ? "package" : "oleObject") is { } packageId &&
                        WriteResource(ole.PreviewResourceId, true) is { } olePreviewId)
                        return WriteOleObject(inline, ole, packageId, olePreviewId, ++drawingId);
                    Loss("ole-unavailable", "Unavailable embedded OLE data or preview", "Alternative text retained; no resource fetched.", id: inline.Id); return null;
                }
                if (inline.Payload is not ImageInlinePayload image || WriteResource(image.ResourceId, true) is not { } relationshipId)
                { Loss("inline-fallback", "Control or unavailable image resource", "Alternative text retained; no resource fetched.", id: inline.Id); return null; }
                var previewId = image.PreviewResourceId is null ? null : WriteResource(image.PreviewResourceId, true);
                if (image.PreviewResourceId is not null && previewId is null) Loss("image-preview", "Unavailable image preview", "Original image retained without the supplied preview.", id: inline.Id);
                if (Math.Abs(inline.Width * 9525 - Math.Round(inline.Width * 9525)) > .000001 || Math.Abs(inline.Height * 9525 - Math.Round(inline.Height * 9525)) > .000001)
                    Loss("dimension-precision", "Sub-EMU image size", "Image dimensions rounded to the nearest EMU.", id: inline.Id);
                return WriteImageDrawing(inline, relationshipId, ++drawingId, previewId, document.Resources[image.ResourceId].MediaType == "image/svg+xml");
            }
            XElement WriteParagraph(Paragraph paragraph, Guid anonymousIdentity)
            {
                token.ThrowIfCancellationRequested();
                var ps = resolver.ResolveParagraphStyle(paragraph.Style);
                var inheritedTabs = paragraph.Style.Overrides?.TabStops.IsSet == true ? resolver.ResolveParagraphStyle(paragraph.Style with { Overrides = paragraph.Style.Overrides with { TabStops = default } }).TabStops : [];
                var pp = WriteParagraphProperties(paragraph.Style, paragraph.Id, inheritedTabs, document.Defaults.Paragraph.DefaultTabWidth > 0 ? document.Defaults.Paragraph.DefaultTabWidth : 48);
                if (ps.List != ListKind.None && (paragraph.Style.Overrides is null || paragraph.Style.Overrides.List.IsSet || paragraph.Style.Overrides.ListId.IsSet || paragraph.Style.Overrides.ListLevel.IsSet)) pp.Add(new XElement(W + "numPr", Val("ilvl", ps.ListLevel), Val("numId", NumberFor(paragraph with { Style = ps }, anonymousIdentity))));
                if (ps.List != ListKind.None)
                {
                    var definition = ps.ListDefinition ?? identities.GetValueOrDefault(ps.ListId ?? anonymousIdentity).Definition;
                    var level = definition?.Level(ps.ListLevel, ps.List);
                    if (level is not null && (level.TextIndent is not null || level.MarkerIndent is not null || level.TabPosition is not null))
                    {
                        if (ps.Indent == 0 && ps.FirstLineIndent == 0)
                            pp.Element(W + "ind")?.Attributes().Where(a => a.Name == W + "left" || a.Name == W + "hanging" || a.Name == W + "firstLine").Remove();
                        else Loss("numbering-paragraph-indent", "Combined direct paragraph and list-level indentation",
                            "Retained both properties; Word gives direct paragraph indentation precedence instead of adding the list indentation.", id: paragraph.Id);
                    }
                }
                if (ps.List == ListKind.None && paragraph.Style.Overrides?.List.IsSet == true) pp.Add(new XElement(W + "numPr", Val("numId", 0)));
                pp.Add(WriteTextStyle(paragraph.DefaultStyle, paragraph.Id, ps.LetterSpacing));
                var p = new XElement(W + "p", pp);
                writtenParagraphs[paragraph.Id] = p;
                foreach (var item in RangeInterchange.Items(document, paragraph, includeForms: true))
                {
                    if (item.Run is not { } run) { p.Add(item.ContentControl is not null || item.Permission is not null ? [WriteFormBoundary(item, permissionIds)] : WriteRangeBoundary(item, bookmarkIds)); continue; }
                    var r = new XElement(W + "r", WriteTextStyle(run.Style, paragraph.Id, ps.LetterSpacing));
                    var mergeField = run.Inline?.Payload as MergeFieldInlinePayload;
                    if (mergeField is not null) MergeFieldInstructions.ReportExportOptions("docx", run.Inline!, mergeField);
                    var pageField = run.Inline?.Payload as PageFieldInlinePayload;
                    if (run.Inline?.Payload is NoteInlinePayload noteReference)
                    {
                        var note = document.Notes.First(n => n.Id == noteReference.NoteId);
                        r.Add(new XElement(W + (note.Kind == DocumentNoteKind.Footnote ? "footnoteReference" : "endnoteReference"),
                            new XAttribute(W + "id", noteNumbers[note.Id]), note.CustomMark is not null ? new XAttribute(W + "customMarkFollows", 1) : null));
                        if (note.CustomMark is not null) r.Add(new XElement(W + "t", note.CustomMark));
                    }
                    else if (mergeField is null && pageField is null && run.Inline is { Payload: not FormControlInlinePayload and not EquationInlinePayload } inline && WriteImage(inline) is { } drawing) r.Add(drawing);
                    else foreach (var segment in Regex.Split(run.PlainText, "([\t\u2028])"))
                        if (segment == "\t") r.Add(new XElement(W + "tab"));
                        else if (segment == "\u2028") r.Add(new XElement(W + "br"));
                        else if (segment.Length > 0) r.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), segment));
                    var content = run.Inline?.Payload is EquationInlinePayload equation ? EquationMarkup.Parse(equation.Xml) :
                        pageField is not null ? new XElement(W + "fldSimple", new XAttribute(W + "instr", pageField.Field switch { PageFieldKind.NumPages => "NUMPAGES", PageFieldKind.SectionPages => "SECTIONPAGES", _ => "PAGE" }), r) :
                        mergeField is null ? r : new XElement(W + "fldSimple", new XAttribute(W + "instr", MergeFieldInstructions.Write(mergeField.Name)), r);
                    if (run.Inline?.Payload is FormControlInlinePayload form)
                        content = new XElement(W + "sdt", WriteContentControlProperties(document.ContentControls.First(c => c.Id == form.ControlId)), new XElement(W + "sdtContent", content));
                    var resolvedStyle = resolver.ResolveText(paragraph, run.Style);
                    if (resolvedStyle.InternalLink is { } destination)
                        p.Add(new XElement(W + "hyperlink", new XAttribute(W + "anchor", destination.BookmarkName),
                            destination.Tooltip is null ? null : new XAttribute(W + "tooltip", destination.Tooltip), new XAttribute(Tx + "activation", destination.Activation), content));
                    else if (resolvedStyle.Hyperlink is { } link)
                    {
                        var id = $"link{relationships.Count}";
                        relationships.Add(new(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/hyperlink"), new XAttribute("Target", link), new XAttribute("TargetMode", "External")));
                        p.Add(new XElement(W + "hyperlink", new XAttribute(R + "id", id), content));
                    }
                    else p.Add(content);
                }
                return p;
            }
            IEnumerable<XElement> WriteBlocks(IEnumerable<Block> blocks)
            {
                var anonymousIdentity = Guid.NewGuid();
                var anonymousBulletIdentity = Guid.NewGuid();
                foreach (var block in blocks)
                {
                    token.ThrowIfCancellationRequested();
                    if (block is Paragraph p)
                    {
                        if (p.Style.List == ListKind.None) { anonymousIdentity = Guid.NewGuid(); anonymousBulletIdentity = Guid.NewGuid(); }
                        yield return WriteParagraph(p, p.Style.List == ListKind.Bullet ? anonymousBulletIdentity : anonymousIdentity);
                    }
                    else if (block is Section section)
                    {
                        if (section.Background is not null || section.BorderColor is not null || section.Borders is not null || section.PaddingEdges is not null || section.Padding != 0)
                            Loss("section-decoration", "Section background, border, or padding", "Section grouping and content retained.", id: section.Id);
                        yield return new XElement(W + "sdt", new XElement(W + "sdtPr", new XAttribute(Tx + "decorativeSection", "1"), Val("tag", SectionTag)), new XElement(W + "sdtContent", WriteBlocks(section.Blocks)));
                    }
                    else if (block is Table table)
                    {
                        var widths = table.ColumnWidths.IsEmpty ? Enumerable.Repeat(600d / table.ColumnCount, table.ColumnCount).ToArray() : table.ColumnWidths.ToArray();
                        var gridExtent = table.PreferredWidth.Unit == TableWidthUnit.Absolute ? table.PreferredWidth.Value * 15 : 9000d;
                        var scale = gridExtent / widths.Sum(); var gridWidths = widths.Select(width => Math.Max(1, (int)Math.Round(width * scale))).ToArray();
                        if (widths.Select((width, i) => Math.Abs(width * scale - gridWidths[i])).Any(delta => delta > .000001)) Loss("dimension-precision", "Relative column width precision", "Column proportions rounded to the exported twip grid.", id: table.Id);
                        var t = new XElement(W + "tbl", new XElement(W + "tblPr", table.StyleId is { } tableStyle ? Val("tblStyle", tableStyle) : null, WritePreferredWidth("tblW", table.PreferredWidth, table.Id)),
                            new XElement(W + "tblGrid", gridWidths.Select(width => new XElement(W + "gridCol", new XAttribute(W + "w", width)))));
                        if (table.StyleOverrides is { } tableOverrides) t.Element(W + "tblPr")!.Add(WriteTableStyle(tableOverrides).Elements());
                        WriteTableProperties(t.Element(W + "tblPr")!, table);
                        var flattenCellFormatting = table.StyleOverrides is { } directTable &&
                            (directTable.Borders.IsSet || directTable.VerticalAlignment.IsSet || directTable.TextDirection.IsSet);
                        if (flattenCellFormatting) Loss("table-cell-formatting", "Table-wide cell formatting overrides", "Effective cell formatting retained as direct cell properties.", id: table.Id);
                        var tableResolver = new DocumentStyleResolver(document);
                        for (var row = 0; row < table.Rows.Length; row++)
                        {
                            var tr = new XElement(W + "tr");
                            var sizing = table.RowSizing.IsEmpty ? new TableRowSizing() : table.RowSizing[row];
                            tr.Add(new XElement(W + "trPr", row < table.RepeatHeaderRows ? new XElement(W + "tblHeader") : null,
                                !sizing.AllowSplit ? new XElement(W + "cantSplit") : null,
                                sizing.Mode != TableRowHeightMode.Auto ? new XElement(W + "trHeight", new XAttribute(W + "val", Twips(sizing.Height)), new XAttribute(W + "hRule", sizing.Mode == TableRowHeightMode.Exact ? "exact" : "atLeast")) : null));
                            for (var col = 0; col < table.ColumnCount; col++)
                            {
                                var owner = table.OwnerOf(row, col); if (owner.Column != col) continue;
                                var cell = table.Rows[owner.Row][owner.Column]; var continuation = owner.Row != row;
                                var explicitVerticalAlignment = cell.StyleOverrides?.VerticalAlignment.IsSet == true || table.StyleOverrides?.VerticalAlignment.IsSet == true;
                                ReportCellFormattingLosses(cell);
                                if (flattenCellFormatting || cell.StyleOverrides is not null)
                                    cell = tableResolver.ResolveTableCell(table, owner.Row, owner.Column);
                                var tc = new XElement(W + "tc", new XElement(W + "tcPr",
                                    WritePreferredWidth("tcW", cell.PreferredWidth, cell.Id),
                                    cell.VerticalAlignment != TableCellVerticalAlignment.Top || explicitVerticalAlignment ? Val("vAlign", cell.VerticalAlignment switch { TableCellVerticalAlignment.Center => "center", TableCellVerticalAlignment.Bottom => "bottom", _ => "top" }) : null,
                                    cell.ColumnSpan > 1 ? Val("gridSpan", cell.ColumnSpan) : null, cell.RowSpan > 1 ? Val("vMerge", continuation ? "continue" : "restart") : null,
                                    cell.Borders is not null ? WriteBorders(cell.Borders, cell.Id) : null,
                                    cell.Background is not null ? new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "fill", Color(cell.Background, cell.Id))) : null,
                                    cell.Padding is not null ? WritePadding("tcMar", cell.Padding) : null));
                                if (continuation) tc.Add(new XElement(W + "p"));
                                else
                                {
                                    tc.Add(WriteBlocks(cell.Blocks));
                                    // Word requires a trailing paragraph after a nested table in a cell.
                                    if (tc.Elements().LastOrDefault()?.Name != W + "p") tc.Add(new XElement(W + "p", new XElement(W + "pPr", Val("pStyle", CellEndStyle))));
                                }
                                tr.Add(tc);
                            }
                            t.Add(tr);
                        }
                        yield return t;
                    }
                }
            }
            void StoryPart(string name, XElement root, string contentType)
            {
                Part("word/" + name + ".xml", root);
                var used = root.DescendantsAndSelf().Attributes().Where(a => a.Name == R + "id" || a.Name == R + "embed").Select(a => a.Value).ToHashSet();
                var storyRelationships = relationships.Where(r => used.Contains((string)r.Attribute("Id")!)).Select(r => new XElement(r)).ToArray();
                if (storyRelationships.Length > 0) Part("word/_rels/" + name + ".xml.rels", new XElement(Rel + "Relationships", storyRelationships));
                storyContentTypes.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/word/" + name + ".xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml." + contentType + "+xml")));
            }
            var usedStories = document.Notes.Select(n => n.StoryId).ToHashSet();
            foreach (var section in document.Sections) foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
                if (section.HeaderFooter.GetReference(footer, variant).StoryId is { } storyId) usedStories.Add(storyId);
            foreach (var story in document.Stories.Values.Where(s => !usedStories.Contains(s.Id)))
                Loss("unreferenced-story", "Secondary story without an owning reference", "Unreferenced story omitted.", id: story.Id);
            var hasWatermarks = document.Sections.Any(section => section.Watermark is not null);
            DocumentWatermark? currentWatermark = null;
            var headerRelationships = new Dictionary<(Guid?, bool, DocumentWatermark?), string>();
            string HeaderRelationship(StoryReference reference, bool footer)
            {
                var key = (reference.StoryId, footer, footer ? null : currentWatermark);
                if (headerRelationships.TryGetValue(key, out var existing)) return existing;
                var name = (footer ? "footer" : "header") + (headerRelationships.Count + 1);
                var blocks = reference.StoryId is { } id ? document.Stories[id].Blocks : [new Paragraph()];
                var root = new XElement(W + (footer ? "ftr" : "hdr"), new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), WriteBlocks(blocks));
                if (!footer && hasWatermarks)
                {
                    root.SetAttributeValue(Tx + "watermarkHeader", reference.StoryId?.ToString() ?? "none");
                    if (currentWatermark is { } watermark)
                    {
                        var imageId = watermark.ResourceId is null ? null : WriteResource(watermark.ResourceId, true);
                        if (watermark.ResourceId is null || imageId is not null) root.Add(WriteWatermarkPicture(watermark, imageId, ++drawingId));
                        else Loss("watermark-image", "Unavailable watermark image", "Watermark omitted; header text retained.");
                    }
                }
                StoryPart(name, root, footer ? "footer" : "header");
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", name), new XAttribute("Type", R.NamespaceName + (footer ? "/footer" : "/header")), new XAttribute("Target", name + ".xml")));
                headerRelationships.Add(key, name); return name;
            }
            var body = new XElement(W + "body", WriteBlocks(document.Blocks));
            if (document.Sections.IsEmpty) body.Add(new XElement(W + "sectPr"));
            else
            {
                for (var index = 0; index < document.Sections.Length; index++)
                {
                    var section = document.Sections[index];
                    currentWatermark = section.Watermark;
                    var exportSection = section;
                    if (hasWatermarks)
                        foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
                            exportSection = exportSection with { HeaderFooter = exportSection.HeaderFooter.WithReference(false, variant,
                                new StoryReference { LinkToPrevious = false, StoryId = document.ResolveHeaderFooter(index, false, variant)?.Id }) };
                    var properties = WritePhysicalSection(exportSection, HeaderRelationship);
                    if (document.Protection.Mode == DocumentProtectionMode.FormsOnly && !document.Protection.ProtectedSectionIds.IsEmpty)
                        properties.Add(Val("formProt", document.Protection.ProtectedSectionIds.Contains(section.Id) ? 1 : 0));
                    if (hasWatermarks) properties.SetAttributeValue(Tx + "headerLinks", string.Join(',', Enum.GetValues<HeaderFooterVariant>().Select(variant => section.HeaderFooter.GetReference(false, variant).LinkToPrevious ? "1" : "0")));
                    if (section.Watermark is { } watermark)
                    {
                        var imageId = watermark.ResourceId is null ? null : WriteResource(watermark.ResourceId, true);
                        if (watermark.ResourceId is null || imageId is not null)
                            properties.Add(new XElement(Tx + "watermark", new XAttribute("data", System.Text.Json.JsonSerializer.Serialize(watermark, JsonDocumentFormat.Options)),
                                imageId is null ? null : new XAttribute(R + "embed", imageId)));
                    }
                    if (index == document.Sections.Length - 1) body.Add(properties);
                    else
                    {
                        var next = writtenParagraphs[document.Sections[index + 1].StartParagraphId];
                        var previous = next.ElementsBeforeSelf().LastOrDefault();
                        if (previous?.Name == W + "p") previous.Element(W + "pPr")!.Add(properties);
                        else next.AddBeforeSelf(new XElement(W + "p", new XElement(W + "pPr", Val("pStyle", CellEndStyle), properties)));
                    }
                }
                if (document.Sections.Select(s => s.HeaderFooter.DifferentOddEvenPages).Distinct().Count() > 1)
                    Loss("odd-even-scope", "Section-specific odd/even option", "DOCX applies the odd/even option document-wide; story references retained.");
                if (document.Sections.Select(s => s.PageSettings.MirrorMargins).Distinct().Count() > 1)
                    Loss("mirror-margin-scope", "Section-specific mirrored margins", "DOCX applies mirrored margins document-wide.");
            }
            foreach (var endnote in new[] { false, true })
            {
                var name = endnote ? "endnotes" : "footnotes";
                var selected = document.Notes.Where(n => n.Kind == (endnote ? DocumentNoteKind.Endnote : DocumentNoteKind.Footnote)).ToArray();
                if (selected.Length == 0) continue;
                var noteSettings = endnote ? document.EndnoteSettings : document.FootnoteSettings;
                XElement Separator(int id, string type, string text) => new(W + (endnote ? "endnote" : "footnote"), new XAttribute(W + "id", id), new XAttribute(W + "type", type),
                    new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))));
                var root = new XElement(W + name, new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R),
                    Separator(-1, "separator", noteSettings.SeparatorText), Separator(0, "continuationSeparator", noteSettings.ContinuationSeparatorText));
                foreach (var note in selected)
                {
                    var content = new XElement(W + (endnote ? "endnote" : "footnote"), new XAttribute(W + "id", noteNumbers[note.Id]), WriteBlocks(document.Stories[note.StoryId].Blocks));
                    var first = content.Descendants(W + "p").FirstOrDefault();
                    if (first is null) { first = new XElement(W + "p"); content.AddFirst(first); }
                    var marker = new XElement(W + "r", new XElement(W + "rPr", Val("vertAlign", "superscript")),
                        note.CustomMark is null ? new XElement(W + (endnote ? "endnoteRef" : "footnoteRef")) : new XElement(W + "t", note.CustomMark));
                    if (first.Element(W + "pPr") is { } pp) pp.AddAfterSelf(marker); else first.AddFirst(marker);
                    root.Add(content);
                }
                StoryPart(name, root, name);
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", name), new XAttribute("Type", R.NamespaceName + "/" + name), new XAttribute("Target", name + ".xml")));
            }
            var placeholders = document.ContentControls.Where(control => control.Placeholder.Length > 0).ToArray();
            if (placeholders.Length != 0)
            {
                Part("word/glossary/document.xml", new XElement(W + "glossaryDocument", new XElement(W + "docParts", placeholders.Select(control =>
                    new XElement(W + "docPart", new XElement(W + "docPartPr", Val("name", "Textalonia.Placeholder." + control.Id.ToString("N")),
                        new XElement(W + "category", Val("name", "General"), Val("gallery", "placeholder"))),
                        new XElement(W + "docPartBody", new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", control.Placeholder)))))))));
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "glossary"), new XAttribute("Type", R.NamespaceName + "/glossaryDocument"), new XAttribute("Target", "glossary/document.xml")));
                storyContentTypes.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/word/glossary/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.glossary+xml")));
            }
            var stylesXml = WriteStyles(document, style => NumberFor(new Paragraph() { Style = style }, style.ListId ?? Guid.NewGuid()));
            Part("word/document.xml", new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), body));
            var hasCoreProperties = document.CoreProperties != new DocumentCoreProperties();
            var hasCustomProperties = document.Properties.Count != 0 || !document.CustomProperties.IsEmpty;
            Part("_rels/.rels", new XElement(Rel + "Relationships", new XElement(Rel + "Relationship", new XAttribute("Id", "document"), new XAttribute("Type", R.NamespaceName + "/officeDocument"), new XAttribute("Target", "word/document.xml")),
                hasCoreProperties ? new XElement(Rel + "Relationship", new XAttribute("Id", "core"), new XAttribute("Type", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"), new XAttribute("Target", "docProps/core.xml")) : null,
                hasCustomProperties ? new XElement(Rel + "Relationship", new XAttribute("Id", "properties"), new XAttribute("Type", R.NamespaceName + "/custom-properties"), new XAttribute("Target", "docProps/custom.xml")) : null));
            if (hasCoreProperties) Part("docProps/core.xml", WriteCoreProperties(document.CoreProperties));
            if (hasCustomProperties) Part("docProps/custom.xml", WriteTypedProperties(document));
            Part("word/settings.xml", new XElement(W + "settings", WriteProtection(document), document.Defaults.Paragraph.DefaultTabWidth > 0 ? Val("defaultTabStop", Twips(document.Defaults.Paragraph.DefaultTabWidth)) : null,
                document.Sections.Any(s => s.HeaderFooter.DifferentOddEvenPages) ? new XElement(W + "evenAndOddHeaders") : null,
                document.Sections.Any(s => s.PageSettings.MirrorMargins) ? new XElement(W + "mirrorMargins") : null,
                WriteNoteSettings(document.FootnoteSettings, false, document.Notes.Any(n => n.Kind == DocumentNoteKind.Footnote)), WriteNoteSettings(document.EndnoteSettings, true, document.Notes.Any(n => n.Kind == DocumentNoteKind.Endnote)),
                document.CompatibilitySettings.Xml is { } compatibilityXml ? XElement.Parse(compatibilityXml, LoadOptions.PreserveWhitespace) : null));
            relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "settings"), new XAttribute("Type", R.NamespaceName + "/settings"), new XAttribute("Target", "settings.xml")));
            var hasFonts = WriteEmbeddedFonts(document, archive, Part);
            if (hasFonts) relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "fontTable"), new XAttribute("Type", R.NamespaceName + "/fontTable"), new XAttribute("Target", "fontTable.xml")));
            var hasTheme = document.Theme.Colors.Count != 0 || document.Theme.Fonts.Count != 0;
            if (hasTheme)
            {
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "theme"), new XAttribute("Type", R.NamespaceName + "/theme"), new XAttribute("Target", "theme/theme1.xml")));
                Part("word/theme/theme1.xml", WriteTheme(document.Theme));
            }
            foreach (var item in document.CustomXmlParts.Select((part, index) => (part, index)))
            {
                RawPart(item.part.PartName, item.part.Xml);
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "customXml" + (item.index + 1)), new XAttribute("Type", R.NamespaceName + "/customXml"), new XAttribute("Target", "../" + item.part.PartName)));
                if (item.part.PropertiesPartName is { } propertiesName)
                {
                    RawPart(propertiesName, item.part.PropertiesXml!);
                    Part("customXml/_rels/" + item.part.PartName["customXml/".Length..] + ".rels", new XElement(Rel + "Relationships",
                        new XElement(Rel + "Relationship", new XAttribute("Id", "itemProps"), new XAttribute("Type", R.NamespaceName + "/customXmlProps"),
                            new XAttribute("Target", propertiesName["customXml/".Length..]))));
                }
            }
            Part("word/_rels/document.xml.rels", new XElement(Rel + "Relationships", relationships));
            Part("word/styles.xml", stylesXml);
            numbering.Add(numbers); Part("word/numbering.xml", numbering);
            Part("[Content_Types].xml", new XElement(Ct + "Types",
                new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                hasFonts ? new XElement(Ct + "Default", new XAttribute("Extension", "odttf"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.obfuscatedFont")) : null,
                hasFonts ? new XElement(Ct + "Override", new XAttribute("PartName", "/word/fontTable.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml")) : null,
                hasTheme ? new XElement(Ct + "Override", new XAttribute("PartName", "/word/theme/theme1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.theme+xml")) : null,
                storyContentTypes,
                hasCoreProperties ? new XElement(Ct + "Override", new XAttribute("PartName", "/docProps/core.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.core-properties+xml")) : null,
                hasCustomProperties ? new XElement(Ct + "Override", new XAttribute("PartName", "/docProps/custom.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.custom-properties+xml")) : null,
                document.CustomXmlParts.Where(part => part.PropertiesPartName is not null).Select(part => new XElement(Ct + "Override", new XAttribute("PartName", "/" + part.PropertiesPartName), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.customXmlProperties+xml"))),
                mediaTypes.Select(m => new XElement(Ct + "Default", new XAttribute("Extension", m.Key), new XAttribute("ContentType", m.Value))),
                new XElement(Ct + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", template ? TemplateMainContentType : DocumentMainContentType)),
                new[] { "styles", "numbering", "settings" }.Select(part => new XElement(Ct + "Override", new XAttribute("PartName", $"/word/{part}.xml"), new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.wordprocessingml.{part}+xml")))));
        }
        return ValidateWrittenPackage(result.ToArray(), token);
    }
}
