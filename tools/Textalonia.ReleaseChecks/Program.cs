using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length is < 5 or > 6)
    throw new ArgumentException("Usage: ReleaseChecks package.nupkg symbols.snupkg version commit output.json [--fetch-sources]");
var fetchSources = args.Length == 6 && args[5] == "--fetch-sources";
if (args.Length == 6 && !fetchSources) throw new ArgumentException("Unknown option.");
using var package = ZipFile.OpenRead(args[0]);
using var symbols = ZipFile.OpenRead(args[1]);
var metadata = XDocument.Parse(ReadText(package, "Textalonia.nuspec")).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
string Value(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;
Require(Value("id") == "Textalonia" && Value("version") == args[2], "Package identity differs from candidate.");
Require(Value("license") == "MIT", "Expected the selected MIT license.");
Require(Value("authors") == "Textalonia contributors", "Unexpected author attribution.");
Require(Value("projectUrl") == "https://github.com/kstanki2/Textalonia", "Unexpected project URL.");
var repository = metadata.Elements().Single(e => e.Name.LocalName == "repository");
Require((string?)repository.Attribute("type") == "git" &&
    (string?)repository.Attribute("url") == "https://github.com/kstanki2/Textalonia" &&
    (string?)repository.Attribute("commit") == args[3] && args[3].Length == 40, "Repository commit is missing or mismatched.");
string[] requiredFiles = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "CHANGELOG.md",
    "docs/RELEASE.md", "docs/RELEASE-NOTES.md", "docs/API-CONTRACTS.md",
    "lib/net8.0/Textalonia.dll", "lib/net8.0/Textalonia.xml", "sources/Themes/Generic.axaml"];
foreach (var file in requiredFiles) Require(package.GetEntry(file) is { Length: > 0 }, $"Missing package content: {file}");
Require(Value("readme") == "README.md", "Missing NuGet readme metadata.");
var dependencies = metadata.Descendants().Where(e => e.Name.LocalName == "dependency")
    .Select(e => new { id = (string)e.Attribute("id")!, version = (string)e.Attribute("version")! }).ToArray();
Require(dependencies.Length == 2 && dependencies.Any(d => d.id == "Avalonia" && d.version == "[12.1.3, 13.0.0)") &&
    dependencies.Any(d => d.id == "AngleSharp" && d.version is "1.8.2" or "[1.8.2, )"), "Unexpected dependencies or minimum versions.");
Require(!package.Entries.Any(e => e.FullName.StartsWith("runtimes/", StringComparison.Ordinal) ||
    e.FullName.Contains("Desktop", StringComparison.OrdinalIgnoreCase)), "Unexpected desktop host or native runtime payload.");

using var dll = new MemoryStream(ReadBytes(package, "lib/net8.0/Textalonia.dll"));
using var pe = new PEReader(dll);
var codeView = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
var codeViewData = pe.ReadCodeViewDebugDirectoryData(codeView);
using var pdbStream = new MemoryStream(ReadBytes(symbols, "lib/net8.0/Textalonia.pdb"));
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
var pdb = provider.GetMetadataReader();
Require(pdb.DebugMetadataHeader is not null, "Missing portable PDB header.");
var pdbId = new BlobContentId(pdb.DebugMetadataHeader!.Id);
Require(pdbId.Guid == codeViewData.Guid && pdbId.Stamp == codeView.Stamp, "Symbols do not match the packaged assembly.");
var checksumEntry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.PdbChecksum);
var checksum = pe.ReadPdbChecksumDebugDirectoryData(checksumEntry);
// Deterministic portable PDB checksums are computed with the 20-byte content ID zeroed.
var checksumBytes = pdbStream.ToArray();
Array.Clear(checksumBytes, pdb.DebugMetadataHeader.IdStartOffset, pdb.DebugMetadataHeader.Id.Length);
Require(checksum.AlgorithmName == "SHA256" &&
    SHA256.HashData(checksumBytes).SequenceEqual(checksum.Checksum), "PDB checksum does not match the assembly.");
var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
var embeddedSourceKind = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
var sourceLinks = pdb.CustomDebugInformation.Select(pdb.GetCustomDebugInformation)
    .Where(c => pdb.GetGuid(c.Kind) == sourceLinkKind).ToArray();
Require(sourceLinks.Length == 1, "Expected exactly one Source Link record.");
using var sourceLink = JsonDocument.Parse(pdb.GetBlobBytes(sourceLinks[0].Value));
var mappings = sourceLink.RootElement.GetProperty("documents").EnumerateObject().ToArray();
Require(mappings.Length > 0 && mappings.All(m => m.Value.GetString()!.StartsWith(
    $"https://raw.githubusercontent.com/kstanki2/Textalonia/{args[3]}/", StringComparison.Ordinal)),
    "Source Link does not target the candidate commit.");
using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.ParseAdd("Textalonia-ReleaseChecks/1.0");
var mappedDocuments = 0;
var embeddedDocuments = 0;
var packagedXamlDocuments = 0;
var fetchedDocuments = 0;
foreach (var handle in pdb.Documents)
{
    var document = pdb.GetDocument(handle);
    var name = pdb.GetString(document.Name);
    var embedded = pdb.GetCustomDebugInformation(handle).Select(pdb.GetCustomDebugInformation)
        .Any(c => pdb.GetGuid(c.Kind) == embeddedSourceKind);
    if (embedded) { embeddedDocuments++; continue; }
    var mapping = mappings.Where(m => m.Name.EndsWith('*') && name.StartsWith(m.Name[..^1], StringComparison.Ordinal)).ToArray();
    // Avalonia 12.1.3 adds an absolute, unmapped XAML sequence-point document.
    // Ship that one theme source explicitly; do not claim debugger Source Link for XAML.
    if (mapping.Length == 0 && name.Replace('\\', '/').EndsWith("/src/Textalonia/Themes/Generic.axaml", StringComparison.Ordinal))
    {
        Require(package.GetEntry("sources/Themes/Generic.axaml") is { Length: > 0 }, "Missing theme source fallback.");
        packagedXamlDocuments++;
        continue;
    }
    Require(mapping.Length == 1, $"Unmapped source document: {name}");
    Require(!document.Hash.IsNil, $"Source document has no checksum: {name}");
    mappedDocuments++;
    if (fetchSources)
    {
        var url = mapping[0].Value.GetString()!.Replace("*", name[(mapping[0].Name.Length - 1)..].Replace('\\', '/'));
        var bytes = await client.GetByteArrayAsync(url);
        var algorithm = pdb.GetGuid(document.HashAlgorithm);
        var hash = algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? SHA256.HashData(bytes)
            : algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? SHA1.HashData(bytes)
            : throw new InvalidDataException("Unknown source checksum algorithm.");
        Require(hash.SequenceEqual(pdb.GetBlobBytes(document.Hash)), $"Published source differs from PDB: {url}");
        fetchedDocuments++;
    }
}
Require(mappedDocuments > 0, "No repository source was mapped.");
var report = new
{
    status = "pass", packageId = Value("id"), version = args[2], commit = args[3], license = Value("license"),
    packageSha256 = Hash(args[0]), symbolsSha256 = Hash(args[1]),
    assemblySha256 = Convert.ToHexString(SHA256.HashData(dll.ToArray())).ToLowerInvariant(),
    pdbId = pdbId.Guid, dependencies, requiredFiles, mappedDocuments, embeddedDocuments, packagedXamlDocuments, fetchedDocuments,
    sourceResolution = fetchSources ? "verified against public source" : "mapping verified; public source retrieval pending"
};
File.WriteAllText(args[4], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Package/source/symbol inspection passed: {args[2]}, {mappedDocuments} mapped sources, {embeddedDocuments} embedded sources.");

static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static byte[] ReadBytes(ZipArchive zip, string path)
{
    using var stream = (zip.GetEntry(path) ?? throw new InvalidDataException($"Missing {path}")).Open();
    using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
}
static string ReadText(ZipArchive zip, string path) => Encoding.UTF8.GetString(ReadBytes(zip, path)).TrimStart('\uFEFF');
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
