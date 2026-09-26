using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Textalonia.Baselines;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ScalableCoreTests
{
    [Fact]
    public void Compatibility_arrays_memoize_identity_without_stale_with_expression_indexes()
    {
        var session = new EditorSession(FlowDocument.FromText("one\ntwo"));
        session.InsertText("new");
        var blocks = session.Document.Blocks;
        Assert.Equal(blocks, session.Document.Blocks);
        Assert.Equal(session.Document, session.Document with { Blocks = blocks });
        var replaced = session.Document with { Blocks = [new Paragraph("different")] };
        Assert.Equal("different", new DocumentIndex(replaced).Text);
        Assert.Equal("newone\ntwo", session.Document.Text);
        Assert.False(session.LastEdit!.Reset);
        session.SelectAll(); session.ToggleBold(); Assert.False(session.LastEdit!.Reset);
        session.Execute(_ => replaced); Assert.True(session.LastEdit!.Reset);
    }

    [Fact]
    public void Typing_coalesces_only_below_the_800ms_boundary_and_navigation_breaks_it()
    {
        var ticks = 0L;
        var session = new EditorSession { Timestamp = () => ticks };
        session.InsertText("a", true);
        ticks += System.Diagnostics.Stopwatch.Frequency * 799 / 1000;
        session.InsertText("b", true);
        ticks += System.Diagnostics.Stopwatch.Frequency * 800 / 1000;
        session.InsertText("c", true);
        session.Undo(); Assert.Equal("ab", session.Index.Text);
        session.Undo(); Assert.Equal("", session.Index.Text);
        session.Redo(); session.Select(2, 2); session.InsertText("d", true);
        session.Undo(); Assert.Equal("ab", session.Index.Text);
    }

    private static IEnumerable<(Paragraph Paragraph, Guid Container, Guid Top)> Flatten(IEnumerable<Block> blocks, Guid container = default, Guid top = default)
    {
        foreach (var block in blocks)
        {
            var owner = top == Guid.Empty ? block.Id : top;
            if (block is Paragraph paragraph) yield return (paragraph, container, owner);
            else if (block is Section section)
                foreach (var entry in Flatten(section.Blocks, section.Id, owner)) yield return entry;
            else if (block is Table table)
                for (var r = 0; r < table.Rows.Length; r++)
                    for (var c = 0; c < table.ColumnCount; c++)
                        if (!table.IsCovered(r, c))
                            foreach (var entry in Flatten(table.Rows[r][c].Paragraphs, table.Rows[r][c].Id, owner)) yield return entry;
        }
    }

    [Theory]
    [InlineData(173)]
    [InlineData(9281)]
    public void Incremental_index_matches_independent_tree_walk_through_structured_edits_and_history(int seed)
    {
        var random = new Random(seed);
        var session = new EditorSession(BaselineDocuments.Structured()) { UndoLimit = 500 };
        for (var step = 0; step < 350; step++)
        {
            var flat = Flatten(session.Document.Blocks).ToArray();
            var text = string.Join('\n', flat.Select(e => e.Paragraph.Text));
            Assert.Equal(text, session.Index.Text);
            Assert.Equal(flat.Length, session.Index.ParagraphCount);
            var start = 0;
            foreach (var entry in flat)
            {
                var actual = session.Index.At(start);
                Assert.Same(entry.Paragraph, actual.Paragraph);
                Assert.Equal(entry.Container, actual.ContainerId); Assert.Equal(entry.Top, actual.TopLevelBlockId);
                Assert.Equal(start, actual.Start); start += entry.Paragraph.Length + 1;
            }
            for (var i = 0; i < 4; i++)
            {
                var from = random.Next(text.Length + 1); var length = random.Next(text.Length - from + 1);
                Assert.Equal(text.Substring(from, length), session.Index.ReadText(from, length));
            }
            if (step % 11 == 0 && session.CanUndo) { session.Undo(); continue; }
            if (step % 13 == 0 && session.CanRedo) { session.Redo(); continue; }
            session.Select(random.Next(text.Length + 1), random.Next(text.Length + 1));
            if (step % 7 == 0) session.ToggleBold();
            else if (step % 17 == 0) session.Execute(d => d with { Blocks = d.Blocks.Add(new Paragraph("external")) });
            else session.InsertText(new[] { "x", "a\nb", "", "\u0301", "👩‍💻", "\u2028", "abc" }[random.Next(7)]);
            session.Document.Validate();
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10000)]
    public void Local_edits_visit_only_the_affected_paragraph_and_share_unrelated_nodes(int count)
    {
        var paragraphs = Enumerable.Range(0, count).Select(i => new Paragraph($"Paragraph {i}" )).ToArray();
        var document = new FlowDocument([new Section { Blocks = paragraphs.Cast<Block>().ToImmutableArray() }]);
        var session = new EditorSession(document);
        var middle = session.Index.ById(paragraphs[count / 2].Id).Start;
        session.Select(middle, middle);
        var old = session.Index;
        var visited = old.VisitedParagraphs;
        session.InsertText("X");
        Assert.InRange(old.VisitedParagraphs - visited, 1, 12);
        Assert.InRange(session.Index.Tree.UpdatedNodes, 1, 3);
        Assert.Same(paragraphs[^1], session.Index.ById(paragraphs[^1].Id).Paragraph);
        Assert.Equal("XParagraph " + count / 2, session.Index.At(middle).Paragraph.Text);
        Assert.Equal("Paragraph " + count / 2, new DocumentIndex(document).At(middle).Paragraph.Text);
    }

    [Fact]
    public void Piece_boundaries_do_not_change_graphemes_or_styled_split_join()
    {
        var value = new string('x', 2047) + "👩‍💻e\u0301🇯🇵🇺🇸\u0600A" + new string('\u0301', 2200) + " z";
        var session = new EditorSession(FlowDocument.FromText(value));
        session.Select(2047, 2047); session.ApplyStyle(s => s with { Bold = true }); session.InsertText("👨‍");
        value = value.Insert(2047, "👨‍");
        var boundaries = StringInfo.ParseCombiningCharacters(value);
        for (var at = 0; at <= value.Length; at++)
        {
            var previous = boundaries.LastOrDefault(b => b < at);
            var next = boundaries.FirstOrDefault(b => b > at, value.Length);
            Assert.Equal(previous, session.PreviousCaret(at));
            Assert.Equal(next, session.NextCaret(at));
        }
        session.Select(0, 0); session.InsertText("\n"); session.DeleteBackward();
        Assert.Equal(value, session.Index.Text);
        session.Document.Validate();
    }

    [Fact]
    public void Large_paste_and_repeated_splits_keep_paths_ordered_and_snapshots_unchanged()
    {
        var session = new EditorSession(FlowDocument.FromText("first\nlast"));
        var original = session.Document;
        session.Select(2, 2);
        var paste = string.Join('\n', Enumerable.Range(0, 2000).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        session.InsertText(paste);
        Assert.Equal("fi" + paste + "rst\nlast", session.Index.Text);
        Assert.All(session.Index.Tree.Paths!.Items(), p => Assert.InRange(p.Value.Key.Scale, 0, 16));
        for (var i = 0; i < 450; i++) { session.Select(1, 1); session.InsertText("\n"); }
        Assert.All(session.Index.Tree.Paths!.Items(), p => Assert.InRange(p.Value.Key.Scale, 0, 256));
        session.Document.Validate();
        Assert.Equal("first\nlast", original.Text);
    }

    [Fact]
    public void Search_streams_across_pieces_runs_and_container_separators()
    {
        var text = new string('a', 2047) + "AbC\nδΕΖ\n" + new string('b', 3000);
        var session = new EditorSession(FlowDocument.FromText(text));
        foreach (var query in new[] { "abc\nδεζ", "aAb", "Ζ\nb", "bb", new string('a', 2100) })
        {
            var expected = new List<int>(); var at = 0;
            while ((at = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            { expected.Add(at); at += query.Length; }
            Assert.Equal(expected, session.FindAll(query).Select(m => m.Start));
        }
    }

    [Fact]
    public async Task Saved_snapshot_exports_stably_while_live_session_edits_shared_text()
    {
        var session = new EditorSession(BaselineDocuments.Workload("paragraphs-1000"));
        var snapshot = session.Document; var expected = DocumentFormats.Json.Serialize(snapshot);
        var export = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                using var stream = new MemoryStream(); await DocumentFormats.Json.SaveAsync(snapshot, stream);
                Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }
        });
        for (var i = 0; i < 200; i++) { session.Select(i, i); session.InsertText("x"); }
        await export;
        Assert.Equal(expected, DocumentFormats.Json.Serialize(snapshot));
    }

    [Fact]
    public void History_budget_counts_shared_storage_once_and_bounds_undo_redo_and_coalescing()
    {
        var session = new EditorSession(FlowDocument.FromText(new string('x', 100000)))
        { HistoryByteLimit = 64 * 1024, UndoLimit = 1000 };
        var states = new Dictionary<int, (FlowDocument Document, TextSelection Selection)>();
        for (var i = 0; i < 300; i++)
        {
            session.Select(i % 3 == 0 ? 0 : session.Index.Length / 2, i % 3 == 0 ? 0 : session.Index.Length / 2);
            states[session.Index.Length] = (session.Document, session.Selection);
            session.InsertText("y");
            Assert.InRange(session.RetainedHistoryBytes, 1, session.HistoryByteLimit);
        }
        var final = session.Document;
        var undone = 0;
        while (session.CanUndo)
        {
            session.Undo(); undone++;
            Assert.Same(states[session.Index.Length].Document, session.Document);
            Assert.Equal(states[session.Index.Length].Selection, session.Selection);
            Assert.InRange(session.RetainedHistoryBytes, 0, session.HistoryByteLimit);
        }
        Assert.InRange(undone, 1, 299);
        while (session.CanRedo) { session.Redo(); Assert.InRange(session.RetainedHistoryBytes, 0, session.HistoryByteLimit); }
        // A tighter budget may evict farthest redo during undo; verify exact state for every retained entry.
        Assert.True(session.Index.Length <= new DocumentIndex(final).Length);
        session.HistoryByteLimit = 0;
        Assert.Equal(0, session.RetainedHistoryBytes); Assert.False(session.CanUndo); Assert.False(session.CanRedo);
        session.HistoryByteLimit = 2048;
        for (var i = 0; i < 100; i++) { session.InsertText("typing", true); Assert.InRange(session.RetainedHistoryBytes, 0, 2048); }
        session.Load(FlowDocument.FromText("reset")); Assert.Equal(0, session.RetainedHistoryBytes);
    }

    [Fact]
    public void History_includes_typing_styles_that_are_not_stored_in_document_runs()
    {
        var session = new EditorSession { HistoryByteLimit = 2048 };
        session.ApplyStyle(style => style with { FontFamily = new string('a', 5000) });
        session.Execute(d => d);
        session.ApplyStyle(_ => TextStyle.Default);
        Assert.False(session.CanUndo); Assert.Equal(0, session.RetainedHistoryBytes);
    }

    [Fact]
    public void Oversized_history_entries_are_evicted_and_document_wrappers_are_collectible()
    {
        var (session, reference) = EvictedSnapshot();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(reference.IsAlive); Assert.False(session.CanUndo); Assert.Equal(0, session.RetainedHistoryBytes);
        GC.KeepAlive(session);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (EditorSession, WeakReference) EvictedSnapshot()
    {
        var session = new EditorSession(FlowDocument.FromText(new string('x', 100000))) { HistoryByteLimit = 1000 };
        var reference = new WeakReference(session.Document);
        session.SelectAll(); session.InsertText("tiny");
        return (session, reference);
    }

    [Fact]
    public void Revision_positions_reject_other_sessions_edits_and_undo()
    {
        var session = new EditorSession(FlowDocument.FromText("abc"));
        var position = session.CreatePosition(2);
        Assert.True(session.TryResolvePosition(position, out var offset)); Assert.Equal(2, offset);
        Assert.False(new EditorSession(session.Document).TryResolvePosition(position, out _));
        session.Select(0, 0); session.InsertText("x"); Assert.False(session.TryResolvePosition(position, out _));
        session.Undo(); Assert.False(session.TryResolvePosition(position, out _));
    }
}
