using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace W15 = "http://schemas.microsoft.com/office/word/2012/wordml";

    private static DocumentContentControl ReadContentControl(XElement element, Func<string, string?>? placeholderText = null)
    {
        var properties = element.Element(W + "sdtPr");
        var metadata = properties?.Attribute(Tx + "control") is { } encoded ? RangeInterchange.Decode<DocumentContentControl>(encoded.Value) : new DocumentContentControl();
        var kind = properties?.Elements().Select(e => e.Name.LocalName).FirstOrDefault(n => n is "text" or "richText" or "checkbox" or "comboBox" or "dropDownList" or "date" or "picture" or "repeatingSection" or "docPartList" or "docPartObj") switch
        {
            "text" => ContentControlKind.PlainText, "checkbox" => ContentControlKind.CheckBox, "comboBox" => ContentControlKind.ComboBox,
            "dropDownList" => ContentControlKind.DropDown, "date" => ContentControlKind.Date, "picture" => ContentControlKind.Picture,
            "repeatingSection" => ContentControlKind.RepeatingSection, "docPartList" or "docPartObj" => ContentControlKind.BuildingBlockGallery,
            _ => ContentControlKind.RichText
        };
        var lockValue = Value(properties?.Element(W + "lock"));
        if (lockValue is not (null or "unlocked" or "contentLocked" or "sdtContentLocked" or "sdtLocked")) throw new FormatException("Unsupported content-control lock value.");
        var text = string.Concat(element.Element(W + "sdtContent")?.Descendants(W + "t").Select(e => e.Value) ?? []);
        var list = properties?.Element(W + (kind == ContentControlKind.ComboBox ? "comboBox" : "dropDownList"));
        var items = list?.Elements(W + "listItem").Select(e => new ContentControlItem((string?)e.Attribute(W + "displayText") ?? (string?)e.Attribute(W + "value") ?? "", (string?)e.Attribute(W + "value") ?? "")).ToImmutableArray() ?? [];
        var binding = properties?.Element(W + "dataBinding");
        var date = properties?.Element(W + "date");
        var control = metadata with
        {
            Kind = kind, Tag = Value(properties?.Element(W + "tag")) ?? "", Title = Value(properties?.Element(W + "alias")) ?? "",
            LockContents = lockValue is "contentLocked" or "sdtContentLocked", LockControl = lockValue is "sdtLocked" or "sdtContentLocked",
            Placeholder = metadata.Placeholder.Length > 0 ? metadata.Placeholder : (Value(properties?.Element(W + "placeholder")?.Element(W + "docPart")) is { } placeholderName ? placeholderText?.Invoke(placeholderName) ?? placeholderName : ""),
            Items = items, IsChecked = properties?.Element(W14 + "checkbox")?.Element(W14 + "checked")?.Attribute(W14 + "val")?.Value is "1" or "true" or "on",
            Value = kind == ContentControlKind.CheckBox ? metadata.Value : kind == ContentControlKind.Date ?
                (((string?)date?.Attribute(W + "fullDate")) is { Length: >= 10 } fullDate ? fullDate[..10] : text) :
                items.FirstOrDefault(i => i.DisplayText == text)?.Value ?? text,
            DateFormat = Value(date?.Element(W + "dateFormat")) ?? metadata.DateFormat,
            Binding = binding is null ? null : new ContentControlBinding { StoreItemId = (string?)binding.Attribute(W + "storeItemID") ?? "",
                XPath = (string?)binding.Attribute(W + "xpath") ?? "", PrefixMappings = (string?)binding.Attribute(W + "prefixMappings") ?? "" }
        };
        if (properties is not null)
        {
            var preserved = new XElement(properties); preserved.Attribute(Tx + "control")?.Remove();
            var raw = preserved.ToString(SaveOptions.DisableFormatting);
            if (raw.Length <= 16384) control = control with { Data = control.Data.SetItem("ooxml.sdtPr", raw) };
            else Loss("content-control-properties", "Oversized additional control properties", "Known bounded properties retained; additional XML metadata omitted.", properties, control.Id);
        }
        if (properties?.Attribute(Tx + "control") is not null && text == ControlDisplay(metadata)) control = control with { Value = metadata.Value };
        if (On(properties?.Element(W + "showingPlcHdr"))) control = control with { Value = "" };
        if (binding is not null) Loss("content-control-binding", "Custom XML content-control binding", "Retained bounded binding metadata and cached value; custom XML data parts are not loaded or evaluated.", binding, control.Id);
        if (kind is ContentControlKind.Picture or ContentControlKind.RepeatingSection or ContentControlKind.BuildingBlockGallery)
            Loss("content-control-interaction", "Picture, repeating-section or gallery control", "Retained content and control metadata; interactive picture replacement, repeating insertion and gallery selection are unavailable.", element, control.Id);
        return control;
    }

    private static string ControlDisplay(DocumentContentControl control) => control.Kind == ContentControlKind.CheckBox ? control.IsChecked ? "\u2612" : "\u2610" :
        control.Items.FirstOrDefault(i => i.Value == control.Value)?.DisplayText ?? (control.Value.Length == 0 ? control.Placeholder : control.Value);

    private static XElement WriteContentControlProperties(DocumentContentControl control)
    {
        var properties = new XElement(W + "sdtPr", new XAttribute(Tx + "control", RangeInterchange.Encode(control)),
            Val("alias", control.Title), Val("tag", control.Tag), Val("id", (BitConverter.ToUInt32(control.Id.ToByteArray()) & 0x7fffffff)),
            control.LockContents || control.LockControl ? Val("lock", control.LockContents ? control.LockControl ? "sdtContentLocked" : "contentLocked" : "sdtLocked") : null);
        if (control.Value.Length == 0 && control.Placeholder.Length > 0 && control.IsAtomic) properties.Add(new XElement(W + "showingPlcHdr"));
        if (control.Placeholder.Length > 0) properties.Add(new XElement(W + "placeholder", Val("docPart", "Textalonia.Placeholder." + control.Id.ToString("N"))));
        if (control.Binding is { } binding)
        {
            properties.Add(new XElement(W + "dataBinding", new XAttribute(W + "storeItemID", binding.StoreItemId), new XAttribute(W + "xpath", binding.XPath), new XAttribute(W + "prefixMappings", binding.PrefixMappings)));
            Loss("content-control-binding", "Custom XML content-control binding", "Retained binding metadata and cached value; custom XML data parts are not exported.", id: control.Id);
        }
        properties.Add(control.Kind switch
        {
            ContentControlKind.PlainText => new XElement(W + "text"),
            ContentControlKind.CheckBox => new XElement(W14 + "checkbox", new XElement(W14 + "checked", new XAttribute(W14 + "val", control.IsChecked ? 1 : 0))),
            ContentControlKind.ComboBox or ContentControlKind.DropDown => new XElement(W + (control.Kind == ContentControlKind.ComboBox ? "comboBox" : "dropDownList"),
                control.Items.Select(item => new XElement(W + "listItem", new XAttribute(W + "displayText", item.DisplayText), new XAttribute(W + "value", item.Value)))),
            ContentControlKind.Date => new XElement(W + "date", string.IsNullOrEmpty(control.Value) ? null : new XAttribute(W + "fullDate", control.Value.Length == 10 ? control.Value + "T00:00:00Z" : control.Value), Val("dateFormat", control.DateFormat)),
            ContentControlKind.Picture => new XElement(W + "picture"),
            ContentControlKind.RepeatingSection => new XElement(W15 + "repeatingSection"),
            ContentControlKind.BuildingBlockGallery => new XElement(W + "docPartList"),
            _ => new XElement(W + "richText")
        });
        if (control.Data.TryGetValue("ooxml.sdtPr", out var preserved))
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(preserved), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16384 });
            var original = XElement.Load(reader);
            foreach (var child in original.Elements().Where(e => e.Name.Namespace != Tx))
            {
                if (properties.Element(child.Name) is { } target)
                {
                    foreach (var attribute in child.Attributes().Where(a => !a.IsNamespaceDeclaration && target.Attribute(a.Name) is null)) target.Add(new XAttribute(attribute));
                    foreach (var value in child.Elements().Where(e => target.Element(e.Name) is null)) target.Add(new XElement(value));
                }
                else if (child.Name.LocalName is not ("alias" or "tag" or "id" or "lock" or "placeholder" or "showingPlcHdr" or "dataBinding" or "text" or "richText" or "checkbox" or "comboBox" or "dropDownList" or "date" or "picture" or "repeatingSection" or "docPartList" or "docPartObj")) properties.Add(new XElement(child));
            }
        }
        if (control.IsLegacyFormField) Loss("legacy-form-export", "Legacy form-field encoding", "Exported an editable structured content control and retained the legacy API flag in native metadata.", id: control.Id);
        return properties;
    }

    private static XElement WriteFormBoundary(RangeInterchange.Item item, IReadOnlyDictionary<Guid, int> permissionIds)
    {
        if (item.ContentControl is { } control)
            return new XElement(Tx + (item.Start ? "controlStart" : "controlEnd"), new XAttribute("id", control.Id), item.Start ? new XAttribute("data", RangeInterchange.Encode(control)) : null);
        var permission = item.Permission!;
        // Native read-only spans and application group names have no equivalent Word permission exception.
        var standard = !permission.IsReadOnly && (permission.Group is null or "everyone" or "administrators" or "contributors" or "editors" or "owners" or "current");
        if (!standard && item.Start) Loss("permission-range", "Read-only span or application permission group", "Exact restrictions retained as native metadata; Word does not enforce this range independently.", id: permission.Id);
        return new XElement(standard ? W + (item.Start ? "permStart" : "permEnd") : Tx + (item.Start ? "permissionStart" : "permissionEnd"),
            new XAttribute(W + "id", permissionIds[permission.Id]), item.Start ? new XAttribute(Tx + "permission", RangeInterchange.Encode(permission)) : null,
            item.Start && standard ? permission.User is not null ? new XAttribute(W + "ed", permission.User) : new XAttribute(W + "edGrp", permission.Group ?? "everyone") : null);
    }

    // Materialize exact inline ranges and complete paragraph ranges as ordinary Word SDTs.
    // Other ranges keep inert native boundary metadata instead of expanding their protection silently.
    private static void WrapContentControls(XElement root)
    {
        foreach (var start in root.Descendants(Tx + "controlStart").Reverse().ToArray())
        {
            var id = (string?)start.Attribute("id");
            var end = root.Descendants(Tx + "controlEnd").FirstOrDefault(e => (string?)e.Attribute("id") == id);
            if (end is null) continue;
            var control = RangeInterchange.Decode<DocumentContentControl>((string)start.Attribute("data")!);
            if (start.Parent == end.Parent && start.NodesAfterSelf().Contains(end))
            {
                var nodes = start.NodesAfterSelf().TakeWhile(n => n != end).ToArray();
                foreach (var node in nodes) node.Remove();
                start.ReplaceWith(new XElement(W + "sdt", WriteContentControlProperties(control), new XElement(W + "sdtContent", nodes))); end.Remove();
                continue;
            }
            var first = start.Parent; var last = end.Parent;
            if (first?.Name == W + "p" && last?.Name == W + "p" && first.Parent == last.Parent &&
                !start.ElementsBeforeSelf().Any(e => e.Name != W + "pPr") && !end.ElementsAfterSelf().Any() && first.ElementsAfterSelf().Contains(last))
            {
                var nodes = first.ElementsAfterSelf().TakeWhile(e => e != last).Prepend(first).Append(last).ToArray();
                var sdt = new XElement(W + "sdt", WriteContentControlProperties(control), new XElement(W + "sdtContent"));
                first.AddBeforeSelf(sdt); start.Remove(); end.Remove();
                foreach (var node in nodes) { node.Remove(); sdt.Element(W + "sdtContent")!.Add(node); }
                continue;
            }
            Loss("content-control-range", "Partial cross-paragraph or cross-container control range", "Exact anchored range and metadata retained as native extension markers; Word displays ordinary content.", id: control.Id);
        }
    }

    private static DocumentProtection ReadProtection(XElement? settings)
    {
        var element = settings?.Element(W + "documentProtection");
        if (element is null) return new();
        var protection = element.Attribute(Tx + "protection") is { } encoded ? RangeInterchange.Decode<DocumentProtection>(encoded.Value) : new DocumentProtection();
        var mode = (string?)element.Attribute(W + "edit");
        if (mode is not (null or "none" or "readOnly" or "forms"))
            Loss("protection-mode", "Unsupported editing restriction " + mode, "Applied read-only restrictions conservatively.", element);
        protection = protection with { Mode = mode switch { null or "none" => DocumentProtectionMode.None, "forms" => DocumentProtectionMode.FormsOnly, _ => DocumentProtectionMode.ReadOnly }, Enforce = element.Attribute(W + "enforcement") is null || OnAttribute(element, "enforcement") };
        if ((string?)element.Attribute(W + "hash") is { } hash && (string?)element.Attribute(W + "salt") is { } salt)
        {
            var algorithm = (string?)element.Attribute(W + "cryptAlgorithmSid") switch { "4" => "Office-SHA1", "12" => "Office-SHA256", "13" => "Office-SHA384", "14" => "Office-SHA512", var sid => "Office-Unknown-" + sid };
            protection = protection with { Password = new DocumentProtectionPassword { Algorithm = algorithm, Hash = hash, Salt = salt, Iterations = (int?)element.Attribute(W + "cryptSpinCount") ?? 100000 } };
        }
        else if (element.Attributes().Any(a => a.Name.LocalName is "hashValue" or "saltValue" or "algorithmName"))
        {
            string? Attribute(string name) => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name && (a.Name.Namespace == W || a.Name.Namespace == W14))?.Value;
            var isoHash = Attribute("hashValue") ?? throw new FormatException("Incomplete protection password verifier.");
            var isoSalt = Attribute("saltValue") ?? throw new FormatException("Incomplete protection password verifier.");
            var name = Attribute("algorithmName") ?? "Unknown";
            protection = protection with { Password = new DocumentProtectionPassword { Algorithm = "Office-" + name.Replace("-", ""),
                Salt = isoSalt, Hash = isoHash, Iterations = Attribute("spinCount") is { } count ? int.Parse(count, CultureInfo.InvariantCulture) : 100000 } };
        }
        else if (element.Attribute(W + "password") is { } legacy)
        {
            var bytes = Convert.FromHexString(legacy.Value.PadLeft(4, '0'));
            if (bytes.Length != 2) throw new FormatException("Invalid legacy protection verifier.");
            protection = protection with { Password = new DocumentProtectionPassword { Algorithm = "Office-Legacy", Salt = "AA==", Hash = Convert.ToBase64String(bytes), Iterations = 0 } };
            Loss("protection-password", "Legacy password verifier", "Editing restrictions and verifier retained; legacy password verification is unavailable and cannot unlock through the editor.", element);
        }
        else if (element.Attribute(W + "hash") is not null || element.Attribute(W + "salt") is not null)
            throw new FormatException("Incomplete protection password verifier.");
        return protection;
    }

    private static XElement? WriteProtection(FlowDocument document)
    {
        var protection = document.Protection;
        if (protection.Mode == DocumentProtectionMode.None && protection.Password is null) return null;
        var element = new XElement(W + "documentProtection", new XAttribute(Tx + "protection", RangeInterchange.Encode(protection)),
            new XAttribute(W + "edit", protection.Mode switch { DocumentProtectionMode.FormsOnly => "forms", DocumentProtectionMode.ReadOnly => "readOnly", _ => "none" }),
            new XAttribute(W + "enforcement", protection.Enforce ? 1 : 0));
        if (!protection.ProtectedSectionIds.IsEmpty)
        {
            element.SetAttributeValue(Tx + "protectedSections", string.Join(",", document.Sections.Select((section, index) => (section, index)).Where(pair => protection.ProtectedSectionIds.Contains(pair.section.Id)).Select(pair => pair.index)));
            if (protection.Mode != DocumentProtectionMode.FormsOnly) Loss("protection-section-mode", "Section-scoped non-form editing protection", "Exact scope retained in native metadata; Word applies the read-only restriction to the whole document.");
        }
        if (protection.Password is { } password)
        {
            var sid = password.Algorithm switch { "Office-SHA1" => 4, "Office-SHA256" => 12, "Office-SHA384" => 13, "Office-SHA512" => 14, _ => 0 };
            if (password.Algorithm == "Office-Legacy")
            {
                element.SetAttributeValue(W + "password", Convert.ToHexString(Convert.FromBase64String(password.Hash)));
                Loss("protection-password", "Legacy password verifier", "Original Word verifier retained; editor password verification is unavailable.");
            }
            else if (sid == 0) Loss("protection-password", "Non-Office password verifier", "Native verifier retained; Word editing protection does not require this password.");
            else element.Add(new XAttribute(W + "cryptProviderType", "rsaAES"), new XAttribute(W + "cryptAlgorithmClass", "hash"), new XAttribute(W + "cryptAlgorithmType", "typeAny"),
                new XAttribute(W + "cryptAlgorithmSid", sid), new XAttribute(W + "cryptSpinCount", password.Iterations), new XAttribute(W + "hash", password.Hash), new XAttribute(W + "salt", password.Salt));
        }
        return element;
    }

    private static void PreparePermissionMarkers(XDocument xml)
    {
        foreach (var marker in xml.Descendants().Where(e => (e.Name == W + "permStart" || e.Name == W + "permEnd") && !e.Ancestors(W + "p").Any()).ToArray())
        {
            var start = marker.Name == W + "permStart";
            var paragraph = start ? marker.ElementsAfterSelf().SelectMany(e => e.DescendantsAndSelf(W + "p")).FirstOrDefault() :
                marker.ElementsBeforeSelf().SelectMany(e => e.DescendantsAndSelf(W + "p")).LastOrDefault();
            if (paragraph is null) throw new FormatException("Permission boundary has no adjacent paragraph.");
            marker.Remove();
            if (start) { if (paragraph.Element(W + "pPr") is { } pp) pp.AddAfterSelf(marker); else paragraph.AddFirst(marker); }
            else paragraph.Add(marker);
        }
    }

    private static void PrepareLegacyForms(XDocument xml)
    {
        foreach (var begin in xml.Descendants(W + "fldChar").Where(e => (string?)e.Attribute(W + "fldCharType") == "begin" && e.Element(W + "ffData") is not null).ToArray())
        {
            var paragraph = begin.Ancestors(W + "p").FirstOrDefault(); var run = begin.Parent;
            if (paragraph is null || run?.Parent != paragraph) continue;
            var following = run.ElementsAfterSelf().ToArray();
            var end = following.FirstOrDefault(e => e.Descendants(W + "fldChar").Any(c => (string?)c.Attribute(W + "fldCharType") == "end"));
            if (end is null) continue;
            var data = begin.Element(W + "ffData")!;
            var box = data.Element(W + "checkBox"); var list = data.Element(W + "ddList");
            var properties = new XElement(W + "sdtPr", Val("tag", Value(data.Element(W + "name")) ?? ""));
            if (box is not null) properties.Add(new XElement(W14 + "checkbox", new XElement(W14 + "checked", new XAttribute(W14 + "val", On(box.Element(W + "checked") ?? box.Element(W + "default")) ? 1 : 0))));
            else if (list is not null) properties.Add(new XElement(W + "dropDownList", list.Elements(W + "listEntry").Select(e => new XElement(W + "listItem", new XAttribute(W + "displayText", Value(e) ?? ""), new XAttribute(W + "value", Value(e) ?? "")))));
            else properties.Add(new XElement(W + "text"));
            var nodes = following.TakeWhile(e => e != end).ToArray();
            var cached = nodes.Where(e => !e.Descendants(W + "instrText").Any() && !e.Descendants(W + "fldChar").Any()).Select(e => new XElement(e)).ToArray();
            var sdt = new XElement(W + "sdt", properties, new XElement(W + "sdtContent", cached));
            var control = ReadContentControl(sdt) with { IsLegacyFormField = true, LockContents = data.Element(W + "enabled") is { } enabled && !On(enabled) };
            if (list is not null && Number(list.Element(W + "result") ?? list.Element(W + "default"), 0) is var selected && selected >= 0 && selected < control.Items.Length)
                control = control with { Value = control.Items[selected].Value };
            if (control.Kind == ContentControlKind.PlainText && control.Value.Length == 0 && Value(data.Element(W + "textInput")?.Element(W + "default")) is { } defaultText)
            {
                control = control with { Value = defaultText };
                sdt.Element(W + "sdtContent")!.Add(new XElement(W + "r", new XElement(W + "t", defaultText)));
            }
            var retained = data.ToString(SaveOptions.DisableFormatting);
            if (retained.Length <= 16384) control = control with { Data = control.Data.SetItem("ooxml.ffData", retained) };
            if (control.LockContents) properties.Add(Val("lock", "contentLocked"));
            properties.SetAttributeValue(Tx + "control", RangeInterchange.Encode(control));
            run.ReplaceWith(sdt); foreach (var node in nodes) node.Remove(); end.Remove();
        }
    }
}
