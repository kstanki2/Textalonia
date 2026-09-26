using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Textalonia.Model;

/// <summary>Maps UTF-16 positions without materializing complete document text.</summary>
public sealed class DocumentIndex
{
    internal DocumentTree Tree { get; }
    /// <summary>Compatibility view; enumerates all visible paragraphs when read.</summary>
    public ImmutableArray<ParagraphPosition> Paragraphs => Enumerate(0, Length).ToImmutableArray();
    /// <summary>Materializes complete visible text. Use ReadText for bounded reads.</summary>
    public string Text { get { FullTextReads++; return ReadText(0, Length); } }
    public int Length => Math.Max(0, Tree.Root.Length - 1);
    public int ParagraphCount => Tree.Root.ParagraphCount;
    internal int VisitedParagraphs { get; private set; }
    internal int FullTextReads { get; private set; }
    public DocumentIndex(FlowDocument document) => Tree = DocumentTree.For(document);

    public ParagraphPosition At(int offset)
    {
        if (Tree.Root.ParagraphCount == 0) throw new InvalidOperationException("Document has no paragraphs.");
        offset = Math.Clamp(offset, 0, Length);
        var node = Tree.Root; var start = 0; var top = Guid.Empty; var container = Guid.Empty;
        while (node.Source is not Paragraph)
        {
            if (node.Source is Section or TableCell) container = node.Id;
            var child = node.Children!.AtLength(offset - start);
            node = child.Value; start += child.Start;
            if (top == Guid.Empty) top = node.Id;
        }
        VisitedParagraphs++;
        return new((Paragraph)node.Source, start, container, top);
    }
    internal ParagraphPosition ById(Guid id)
    {
        var entry = Tree.Locate(id);
        return new((Paragraph)entry.Node.Source!, entry.Start, entry.Container, entry.Top);
    }
    // Offset pruning descends only into intersecting subtrees (including empty paragraphs).
    internal IEnumerable<ParagraphPosition> Enumerate(int start, int end)
    {
        IEnumerable<ParagraphPosition> Visit(DocumentNode node, int offset, Guid container, Guid top)
        {
            if (offset > end || offset + node.Length <= start) yield break;
            if (node.Source is Paragraph paragraph)
            { VisitedParagraphs++; yield return new(paragraph, offset, container, top); yield break; }
            if (node.Source is Section or TableCell) container = node.Id;
            if (node.Children is not null)
                foreach (var entry in VisitChildren(node.Children, offset, container, top)) yield return entry;
        }
        IEnumerable<ParagraphPosition> VisitChildren(StorageTree<OrderKey, DocumentNode> tree, int offset, Guid container, Guid top)
        {
            if (offset > end || offset + tree.Length <= start) yield break;
            if (tree.Left is not null)
                foreach (var entry in VisitChildren(tree.Left, offset, container, top)) yield return entry;
            offset += tree.Left?.Length ?? 0;
            foreach (var entry in Visit(tree.Value, offset, container, top == Guid.Empty ? tree.Value.Id : top)) yield return entry;
            offset += tree.Value.Length;
            if (tree.Right is not null)
                foreach (var entry in VisitChildren(tree.Right, offset, container, top)) yield return entry;
        }
        return Visit(Tree.Root, 0, Guid.Empty, Guid.Empty);
    }
    private IEnumerable<ReadOnlyMemory<char>> Chunks(int start, int length)
    {
        if (length == 0) yield break;
        var end = start + length;
        foreach (var entry in Enumerate(start, end - 1))
        {
            var from = Math.Max(0, start - entry.Start);
            var to = Math.Min(entry.Paragraph.Length, end - entry.Start);
            if (to > from)
                foreach (var chunk in ParagraphText.For(entry.Paragraph).Chunks(from, to - from)) yield return chunk;
            if (entry.End >= start && entry.End < end && entry.End < Length) yield return "\n".AsMemory();
        }
    }
    public string ReadText(int start, int length)
    {
        if (start < 0 || length < 0 || start > Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        return string.Create(length, (Index: this, Start: start), static (span, state) =>
        { var written = 0; CopyNode(state.Index.Tree.Root, 0, state.Start, state.Start + span.Length, span, ref written); });
    }
    private static void CopyNode(DocumentNode node, int offset, int start, int end, Span<char> target, ref int written)
    {
        if (offset >= end || offset + node.Length <= start) return;
        if (node.Source is Paragraph paragraph)
        {
            var from = Math.Max(0, start - offset); var to = Math.Min(paragraph.Length, end - offset);
            if (to > from) { ParagraphText.For(paragraph).CopyTo(from, target.Slice(written, to - from)); written += to - from; }
            if (offset + paragraph.Length >= start && offset + paragraph.Length < end) target[written++] = '\n';
        }
        else if (node.Children is not null) CopyChildren(node.Children, offset, start, end, target, ref written);
    }
    private static void CopyChildren(StorageTree<OrderKey, DocumentNode> tree, int offset, int start, int end, Span<char> target, ref int written)
    {
        if (offset >= end || offset + tree.Length <= start) return;
        if (tree.Left is not null) CopyChildren(tree.Left, offset, start, end, target, ref written);
        offset += tree.Left?.Length ?? 0;
        CopyNode(tree.Value, offset, start, end, target, ref written);
        offset += tree.Value.Length;
        if (tree.Right is not null) CopyChildren(tree.Right, offset, start, end, target, ref written);
    }
    public char CharAt(int offset)
    {
        if ((uint)offset >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var entry = At(offset);
        return offset == entry.End ? '\n' : ParagraphText.For(entry.Paragraph)[offset - entry.Start];
    }
    internal IEnumerable<int> Find(string query, StringComparison comparison)
    {
        if (query.Length == 0) yield break;
        var carry = ""; var consumed = 0; var nextMatch = 0;
        foreach (var chunk in Chunks(0, Length))
        {
            var window = carry + chunk.ToString();
            var origin = consumed - carry.Length;
            var from = Math.Max(0, nextMatch - origin);
            while (from <= window.Length - query.Length)
            {
                var found = window.IndexOf(query, from, comparison);
                if (found < 0) break;
                yield return origin + found;
                from = found + query.Length; nextMatch = origin + from;
            }
            consumed += chunk.Length;
            carry = window[^Math.Min(query.Length - 1, window.Length)..];
        }
    }
    internal int Snap(int position)
    {
        position = Math.Clamp(position, 0, Length);
        var entry = At(position);
        return entry.Start + ParagraphText.Snap(entry.Paragraph, position - entry.Start);
    }
    internal int PreviousCaret(int position) => position <= 0 ? 0 : Snap(Math.Min(position, Length) - 1);
    internal int NextCaret(int position)
    {
        position = Math.Clamp(position, 0, Length);
        if (position == Length) return Length;
        var entry = At(position);
        if (position == entry.End) return position + 1;
        return entry.Start + ParagraphText.Next(entry.Paragraph, position - entry.Start);
    }
}

internal static class ParagraphText
{
    private static readonly ConditionalWeakTable<Paragraph, PieceText> Texts = new();
    public static PieceText For(Paragraph paragraph) => Texts.GetValue(paragraph, p =>
    {
        var text = PieceText.Empty;
        foreach (var run in p.Runs) text = PieceText.Join(text, run.Storage);
        return text;
    });
    // Two adjacent printable ASCII characters guarantee a grapheme boundary. Scan
    // through the entire context when necessary (RI parity, prepend, ZWJ, combining
    // chains). Piece/style boundaries are never treated as grapheme boundaries.
    private static (int Start, int[] Breaks, int End) Boundaries(Paragraph paragraph, int position)
    {
        var text = For(paragraph);
        bool Safe(int at) => at == 0 || at == text.Length ||
            text[at - 1] is >= ' ' and <= '~' && text[at] is >= ' ' and <= '~';
        var start = Math.Min(position, text.Length);
        while (!Safe(start)) start--;
        var end = Math.Min(position + 1, text.Length);
        while (!Safe(end)) end++;
        return (start, StringInfo.ParseCombiningCharacters(text.Read(start, end - start)), end);
    }
    public static int Snap(Paragraph paragraph, int position)
    {
        if (position == 0 || position == paragraph.Length) return position;
        var (start, breaks, _) = Boundaries(paragraph, position);
        var index = Array.BinarySearch(breaks, position - start);
        return start + breaks[index >= 0 ? index : Math.Max(0, ~index - 1)];
    }
    public static int Next(Paragraph paragraph, int position)
    {
        var (start, breaks, end) = Boundaries(paragraph, position);
        var index = Array.BinarySearch(breaks, position - start);
        index = index >= 0 ? index + 1 : ~index;
        return index < breaks.Length ? start + breaks[index] : end;
    }
}
