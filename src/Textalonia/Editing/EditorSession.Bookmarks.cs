using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Creates a bookmark around the active selection. Names are unique and ordinal.</summary>
    public DocumentBookmark? AddBookmark(string name)
    {
        if (IsReadOnly) return null;
        if (!InlineDescriptor.ValidKey(name)) throw new ArgumentException("Enter a nonempty bookmark name of at most 256 characters.", nameof(name));
        if (Document.Bookmarks.Any(b => b.Name == name)) throw new ArgumentException("The bookmark name already exists.", nameof(name));
        var bookmark = new DocumentBookmark { Name = name,
            Start = DocumentAnchor.Create(Document, ActiveStoryId, Selection.Start, Selection.IsEmpty ? AnchorAffinity.After : AnchorAffinity.Before),
            End = DocumentAnchor.Create(Document, ActiveStoryId, Selection.End) };
        var document = Document with { Bookmarks = Document.Bookmarks.Add(bookmark) };
        document.Validate(); Commit(document, Selection, wholeDocument: true);
        return bookmark;
    }

    public void RenameBookmark(string name, string newName)
    {
        if (IsReadOnly) return;
        if (!InlineDescriptor.ValidKey(newName)) throw new ArgumentException("Invalid bookmark name.", nameof(newName));
        var bookmark = Document.Bookmarks.FirstOrDefault(b => b.Name == name) ?? throw new ArgumentException("The bookmark does not exist.", nameof(name));
        if (name == newName) return;
        if (Document.Bookmarks.Any(b => b.Name == newName)) throw new ArgumentException("The bookmark name already exists.", nameof(newName));
        InternalLinkDestination? RenameLink(InternalLinkDestination? link) => link?.BookmarkName == name
            ? link with { BookmarkName = newName } : link;
        TextStyleOverrides RenameOverrides(TextStyleOverrides overrides) => overrides.InternalLink.IsSet
            ? overrides with { InternalLink = new(RenameLink(overrides.InternalLink.Value)) } : overrides;
        TextStyle Rename(TextStyle style) => style with
        { InternalLink = RenameLink(style.InternalLink), Overrides = style.Overrides is { } overrides ? RenameOverrides(overrides) : null };
        ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.Select<Block, Block>(block => block switch
        {
            Paragraph p => p with { DefaultStyle = Rename(p.DefaultStyle), Runs = p.Runs.Select(r => r with { Style = Rename(r.Style) }).ToImmutableArray() },
            Section section => section with { Blocks = Rewrite(section.Blocks) },
            Table table => table with { Rows = table.Rows.Select(row => row.Select(cell => cell with
                { Blocks = Rewrite(cell.Blocks), MergeOriginalBlocks = Rewrite(cell.MergeOriginalBlocks) }).ToImmutableArray()).ToImmutableArray() },
            _ => block
        }).ToImmutableArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = newName };
        string RewriteInstruction(string instruction)
        {
            try { return Textalonia.Model.Fields.FieldInstructionParser.RewriteBookmarkReferences(instruction, names); }
            catch (FormatException) { return instruction; }
        }
        var updated = Document with
        {
            Blocks = Rewrite(Document.Blocks),
            Bookmarks = Document.Bookmarks.Replace(bookmark, bookmark with { Name = newName }),
            Fields = Document.Fields.Select(field => field with { Instruction = RewriteInstruction(field.Instruction) }).ToImmutableArray(),
            Stories = Document.Stories.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Blocks = Rewrite(pair.Value.Blocks) }),
            Defaults = Document.Defaults with { Text = Rename(Document.Defaults.Text) },
            Styles = Document.Styles with
            {
                Characters = Document.Styles.Characters.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with
                    { Formatting = RenameOverrides(pair.Value.Formatting) }),
                Paragraphs = Document.Styles.Paragraphs.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with
                    { TextFormatting = RenameOverrides(pair.Value.TextFormatting) })
            }
        };
        updated.Validate(); Commit(updated, Selection, wholeDocument: true);
    }

    public void DeleteBookmark(string name)
    {
        if (IsReadOnly) return;
        var bookmark = Document.Bookmarks.FirstOrDefault(b => b.Name == name) ?? throw new ArgumentException("The bookmark does not exist.", nameof(name));
        Commit(Document with { Bookmarks = Document.Bookmarks.Remove(bookmark) }, Selection, wholeDocument: true);
    }

    public bool NavigateToBookmark(string name)
    {
        var bookmark = Document.Bookmarks.FirstOrDefault(b => b.Name == name);
        if (bookmark is null) return false;
        var start = bookmark.Start.Resolve(Document); var end = bookmark.End.Resolve(Document);
        SwitchStory(bookmark.Start.StoryId); Select(start, end); return true;
    }
}
