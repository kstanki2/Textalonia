using System.Globalization;
using System.Text.Json;
using Textalonia.Baselines;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class BaselineCorpusTests
{
    public static IEnumerable<object[]> Fixtures => BaselineDocuments.Names.Select(n => new object[] { n });
    private static string Encode(FlowDocument document) => DocumentFormats.Json.Serialize(document);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Deterministic_fixtures_validate_round_trip_and_keep_original_snapshots(string name)
    {
        var original = BaselineDocuments.Create(name);
        original.Validate();
        var json = Encode(original);
        Assert.Equal(json, Encode(BaselineDocuments.Create(name)));
        var loaded = DocumentFormats.Json.Parse(json);
        loaded.Validate();
        Assert.Equal(json, Encode(loaded));
        var session = new EditorSession(loaded);
        session.Select(session.Index.Length, 0);
        session.ToggleBold();
        session.Document.Validate();
        Assert.Equal(json, Encode(original));
        session.Undo();
        Assert.Equal(json, Encode(session.Document));
        Assert.Equal(new TextSelection(original.Text.Length, 0), session.Selection);
    }

    [Fact]
    public void Frozen_v1_fixture_preserves_all_fields_and_merge_restoration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-v1.json");
        var json = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd();
        var loaded = DocumentFormats.Json.Parse(json);
        Assert.Equal(json, Encode(loaded).Replace("\r\n", "\n"));
        Assert.Equal(Encode(BaselineDocuments.Structured()), Encode(loaded));
        var section = Assert.IsType<Section>(loaded.Blocks[1]);
        var table = Assert.IsType<Table>(section.Blocks[^1]);
        var split = table.SplitCell(1, 1);
        Assert.Equal(Encode(new FlowDocument([BaselineDocuments.Grid(3, 3, "fixture")])), Encode(new FlowDocument([split])));
    }

    public sealed record Operation(string Kind, int Anchor, int Active, string Text, int Value);
    public sealed record Replay(int Version, int Seed, bool Structured, List<Operation> Operations);

    [Theory]
    [InlineData(7401, false)]
    [InlineData(1729, false)]
    [InlineData(8675309, false)]
    [InlineData(7401, true)]
    [InlineData(1729, true)]
    [InlineData(8675309, true)]
    public void Fixed_seed_edits_validate_every_snapshot_and_restore_content_and_selection(int seed, bool structured)
    {
        var replayPath = Environment.GetEnvironmentVariable("TEXTALONIA_REPLAY");
        if (!string.IsNullOrEmpty(replayPath))
        {
            var replay = JsonSerializer.Deserialize<Replay>(File.ReadAllText(replayPath))!;
            Assert.Equal(1, replay.Version);
            if (seed == 7401 && !structured) Run(replay.Seed, replay.Structured, replay.Operations.Count, replay.Operations);
            return;
        }
        var setting = Environment.GetEnvironmentVariable("TEXTALONIA_FUZZ_STEPS");
        var count = 120;
        if (!string.IsNullOrEmpty(setting))
            Assert.True(int.TryParse(setting, out count), "TEXTALONIA_FUZZ_STEPS must be an integer.");
        Assert.InRange(count, 1, 100_000);
        Run(seed, structured, count);
    }

    private static void Run(int seed, bool structured, int count, List<Operation>? replay = null)
    {
        var random = new Random(seed);
        var editor = new EditorSession(structured ? BaselineDocuments.Structured() : BaselineDocuments.Create("mixed-scripts"));
        var oracle = editor.Index.Text;
        var log = new List<Operation>();
        var beforeJson = Encode(editor.Document);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var operation = replay is null ? Generate(random, editor, structured, i) : replay[i];
                log.Add(operation);
                editor.Select(operation.Anchor, operation.Active);
                var before = editor.Document;
                beforeJson = Encode(before);
                var selection = editor.Selection;
                var typingStyle = editor.TypingStyle;
                var revision = editor.Revision;
                if (!structured) oracle = ExpectedText(oracle, selection, operation);
                Apply(editor, operation);
                editor.Document.Validate();
                var after = Encode(editor.Document);
                var afterSelection = editor.Selection;
                var afterStyle = editor.TypingStyle;
                Assert.Equal(beforeJson, Encode(before));
                Assert.Equal(editor.Document.Text, editor.Index.Text);
                if (!structured) Assert.Equal(oracle, editor.Index.Text);
                Assert.All(editor.Index.Paragraphs, p => Assert.Equal(p.Paragraph.Length, p.Paragraph.Runs.Sum(r => r.Text.Length)));
                var restored = DocumentFormats.Json.Parse(after);
                restored.Validate();
                Assert.Equal(after, Encode(restored));
                if (editor.Revision == revision) continue;
                editor.Undo();
                editor.Document.Validate();
                Assert.Same(before, editor.Document);
                Assert.Equal(beforeJson, Encode(editor.Document));
                Assert.Equal(selection, editor.Selection);
                Assert.Equal(typingStyle, editor.TypingStyle);
                editor.Redo();
                editor.Document.Validate();
                Assert.Equal(after, Encode(editor.Document));
                Assert.Equal(afterSelection, editor.Selection);
                Assert.Equal(afterStyle, editor.TypingStyle);
            }
        }
        catch (Exception error)
        {
            var output = Environment.GetEnvironmentVariable("TEXTALONIA_FAILURE_DIR") ?? Path.Combine("artifacts", "fixture-failures");
            Directory.CreateDirectory(output);
            var stem = Path.Combine(output, $"seed-{seed}-{(structured ? "structured" : "text")}");
            File.WriteAllText(stem + ".replay.json", JsonSerializer.Serialize(new Replay(1, seed, structured, log), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(stem + ".before.json", beforeJson);
            File.WriteAllText(stem + ".error.txt", $"Seed {seed}; operation {log.Count - 1}\n{error}");
            throw new InvalidOperationException($"Seed {seed}, structured={structured}, operation {log.Count - 1}. Replay: {Path.GetFullPath(stem + ".replay.json")}", error);
        }
    }

    private static Operation Generate(Random random, EditorSession editor, bool structured, int index)
    {
        string[] kinds = structured
            ? ["insert", "backspace", "delete", "format", "list", "merge", "split", "roundtrip", "insert-table", "delete-table"]
            : ["insert", "backspace", "delete", "format", "list", "roundtrip"];
        // Cycle operation classes so every fixed seed necessarily covers all classes.
        var kind = kinds[index % kinds.Length];
        var boundaries = StringInfo.ParseCombiningCharacters(editor.Index.Text).Append(editor.Index.Length).ToArray();
        var anchor = boundaries[random.Next(boundaries.Length)];
        var active = random.Next(3) == 0 ? boundaries[random.Next(boundaries.Length)] : anchor;
        if (structured && kind is "insert" or "backspace" or "delete")
        {
            // Keep containers for subsequent structural operations. Dedicated tests cover cross-container replacement.
            var p = editor.Index.Paragraphs[random.Next(editor.Index.Paragraphs.Length)];
            var local = StringInfo.ParseCombiningCharacters(p.Paragraph.Text).Append(p.Paragraph.Length).ToArray();
            anchor = p.Start + local[random.Next(local.Length)];
            active = anchor;
            if (kind == "backspace" && anchor == p.Start) kind = "insert";
            if (kind == "delete" && anchor == p.End) kind = "insert";
        }
        string[] text = ["", "a", "中文", "👩‍💻", "e\u0301", "x\r\ny", "\u2028", "🇯🇵", "\0q"];
        return new(kind, anchor, active, text[random.Next(text.Length)], random.Next(1, 4));
    }

    private static string ExpectedText(string text, TextSelection selection, Operation operation)
    {
        var start = selection.Start;
        var end = selection.End;
        if (operation.Kind is not ("insert" or "backspace" or "delete")) return text;
        if (start == end && operation.Kind == "backspace")
            start = StringInfo.ParseCombiningCharacters(text).Where(p => p < start).DefaultIfEmpty(0).Last();
        if (start == end && operation.Kind == "delete")
            end = StringInfo.ParseCombiningCharacters(text).FirstOrDefault(p => p > end, text.Length);
        var inserted = operation.Kind == "insert" ? operation.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "") : "";
        return text[..start] + inserted + text[end..];
    }

    private static IEnumerable<Table> Tables(IEnumerable<Block> blocks) => blocks.SelectMany(b => b switch
    {
        Table t => new[] { t }, Section s => Tables(s.Blocks), _ => []
    });

    private static void Apply(EditorSession editor, Operation operation)
    {
        switch (operation.Kind)
        {
            case "insert": editor.InsertText(operation.Text); break;
            case "backspace": editor.DeleteBackward(); break;
            case "delete": editor.DeleteForward(); break;
            case "format": editor.ApplyStyle(s => s with { Bold = !s.Bold, FontSize = 12 + operation.Value }); break;
            case "list": editor.ToggleList((ListKind)(operation.Value % 3)); break;
            case "roundtrip": editor.Execute(d => DocumentFormats.Json.Parse(Encode(d))); break;
            case "insert-table":
                if (Tables(editor.Document.Blocks).Count() < 4) editor.InsertTable(operation.Value, 2);
                break;
            case "delete-table":
                if (Tables(editor.Document.Blocks).Count() > 1) editor.DeleteCurrentTable();
                break;
            case "merge":
            case "split":
                var table = Tables(editor.Document.Blocks).FirstOrDefault();
                if (table is null) break;
                var anchor = table.Rows[0][0];
                var merged = anchor.RowSpan > 1 || anchor.ColumnSpan > 1;
                if (operation.Kind == "merge" && !merged)
                    editor.Execute(d => d.ReplaceBlock(table.Id, table.MergeCells(0, 0, Math.Min(2, table.Rows.Length), Math.Min(2, table.ColumnCount))));
                else if (operation.Kind == "split" && merged)
                    editor.Execute(d => d.ReplaceBlock(table.Id, table.SplitCell(0, 0)));
                break;
            default: throw new InvalidOperationException($"Unknown replay operation: {operation.Kind}");
        }
    }
}
