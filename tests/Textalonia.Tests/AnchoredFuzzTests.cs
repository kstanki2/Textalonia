using System.Collections.Immutable;
using System.Globalization;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public sealed class AnchoredFuzzTests
{
    [Theory]
    [InlineData(50819)]
    [InlineData(91733)]
    public void Seeded_story_edits_keep_ranges_attached_through_graphemes_splits_joins_and_history(int seed)
    {
        var table = Table.Create(2, 1)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("left e\u0301")] })
            .SetCell(1, 0, new TableCell { Blocks = [new Paragraph("right \U0001F600")] });
        var header = new DocumentStory { Kind = DocumentStoryKind.Header,
            Blocks = [new Paragraph("header \U0001F469\u200D\U0001F4BB"), new Paragraph("end")] };
        var original = new FlowDocument([new Paragraph("alpha \U0001F469\u200D\U0001F4BB"),
            new Section { Blocks = [new Paragraph("middle e\u0301"), table] }, new Paragraph("omega")])
        { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header) };
        DocumentAnchor Anchor(Guid story, int offset, AnchorAffinity affinity) => DocumentAnchor.Create(original, story, offset, affinity);
        var bodyLength = new DocumentIndex(original).Length;
        var headerLength = original.GetStoryIndex(header.Id).Length;
        original = original with
        {
            Bookmarks = [
                new() { Name = "body", Start = Anchor(Guid.Empty, 0, AnchorAffinity.Before), End = Anchor(Guid.Empty, bodyLength, AnchorAffinity.After) },
                new() { Name = "header", Start = Anchor(header.Id, 0, AnchorAffinity.Before), End = Anchor(header.Id, headerLength, AnchorAffinity.After) },
                new() { Name = "before", Start = Anchor(Guid.Empty, 5, AnchorAffinity.Before), End = Anchor(Guid.Empty, 5, AnchorAffinity.Before) },
                new() { Name = "after", Start = Anchor(header.Id, 6, AnchorAffinity.After), End = Anchor(header.Id, 6, AnchorAffinity.After) }
            ],
            Fields = [
                new() { Instruction = "UNKNOWN outer", Start = Anchor(Guid.Empty, 0, AnchorAffinity.Before), End = Anchor(Guid.Empty, bodyLength, AnchorAffinity.After) },
                new() { Instruction = "MERGEFIELD Inner", Start = Anchor(Guid.Empty, 1, AnchorAffinity.Before), End = Anchor(Guid.Empty, 5, AnchorAffinity.After) },
                new() { Instruction = "DATE", Start = Anchor(header.Id, 0, AnchorAffinity.Before), End = Anchor(header.Id, 6, AnchorAffinity.After) }
            ]
        };
        var bookmarkIds = original.Bookmarks.Select(b => b.Id).Order().ToArray();
        var fieldIds = original.Fields.Select(f => f.Id).Order().ToArray();
        var session = new EditorSession(original) { UndoLimit = 512 };
        var random = new Random(seed);
        var insertions = new[] { "X", "\n", "\n\n", "e\u0301", "\U0001F469\u200D\U0001F4BB", "\U0001F1FA\U0001F1F8", "中", "\u0301", "x\ny" };
        ValidateSnapshot();
        for (var step = 0; step < 180; step++)
        {
            if (step % 13 == 0) session.SwitchStory(session.ActiveStoryId == Guid.Empty ? header.Id : Guid.Empty);
            var boundaries = Boundaries(session.ActiveDocument);
            var at = random.Next(boundaries.Length);
            var end = Math.Min(boundaries.Length - 1, at + random.Next(4));
            session.Select(boundaries[at], boundaries[at]);
            var before = session.Document;
            switch (step % 8)
            {
                case 0: session.InsertText(insertions[random.Next(insertions.Length)]); break;
                case 1: session.Select(boundaries[end], boundaries[at]); session.InsertText(""); break;
                case 2: session.InsertParagraph(); break;
                case 3: session.DeleteBackward(); break;
                case 4: session.DeleteForward(); break;
                case 5: session.Select(boundaries[end], boundaries[at]); session.InsertText(insertions[random.Next(insertions.Length)]); break;
                case 6: session.Undo(); break;
                case 7: session.Redo(); break;
            }
            ValidateSnapshot();
            if (step % 8 < 6 && !ReferenceEquals(before, session.Document))
            {
                var after = session.Document;
                session.Undo(); ValidateSnapshot(); Assert.Same(before, session.Document);
                session.Redo(); ValidateSnapshot(); Assert.Same(after, session.Document);
            }
        }
        void ValidateSnapshot()
        {
            var document = session.Document;
            document.Validate();
            Assert.Equal(bookmarkIds, document.Bookmarks.Select(b => b.Id).Order());
            Assert.Equal(fieldIds, document.Fields.Select(f => f.Id).Order());
            var ids = new HashSet<Guid>();
            void Identify(Guid id) { Assert.NotEqual(Guid.Empty, id); Assert.True(ids.Add(id), $"Duplicate identifier {id} in seed {seed}."); }
            void Visit(IEnumerable<Block> blocks)
            {
                foreach (var block in blocks)
                {
                    Identify(block.Id);
                    switch (block)
                    {
                        case Paragraph paragraph:
                            foreach (var run in paragraph.Runs) if (run.Inline is { } inline) Identify(inline.Id);
                            break;
                        case Section section: Visit(section.Blocks); break;
                        case Table value:
                            foreach (var row in value.Rows) foreach (var cell in row)
                            { Identify(cell.Id); Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); }
                            break;
                    }
                }
            }
            Visit(document.Blocks);
            foreach (var story in document.Stories.Values) { Identify(story.Id); Visit(story.Blocks); }
            foreach (var bookmark in document.Bookmarks) { Identify(bookmark.Id); CheckRange(bookmark.Start, bookmark.End); }
            foreach (var field in document.Fields) { Identify(field.Id); CheckRange(field.Start, field.End); }
            void CheckRange(DocumentAnchor start, DocumentAnchor end)
            {
                Assert.Equal(start.StoryId, end.StoryId);
                var from = start.Resolve(document); var to = end.Resolve(document);
                Assert.True(from <= to, $"Reversed range in seed {seed}.");
                var valid = Boundaries(document.GetStoryDocument(start.StoryId));
                Assert.Contains(from, valid); Assert.Contains(to, valid);
            }
        }
    }

    private static int[] Boundaries(FlowDocument document) => new DocumentIndex(document).Paragraphs
        .SelectMany(p => StringInfo.ParseCombiningCharacters(p.Paragraph.Text).Select(offset => p.Start + offset)
            .Append(p.End)).Distinct().Order().ToArray();
}
