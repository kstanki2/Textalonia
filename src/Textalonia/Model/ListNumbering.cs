using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Textalonia.Model;

public enum ListMarkerStyle { Decimal, LowerLetter, UpperLetter, LowerRoman, UpperRoman, Bullet }

/// <summary>Numbering at one zero-based list level. Ancestors use their own level's number format.</summary>
public sealed record ListLevelDefinition
{
    public int Start { get; init; } = 1;
    public ListKind Kind { get; init; } = ListKind.Numbered;
    public ListMarkerStyle Marker { get; init; }
    public string? Text { get; init; }
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = ".";
    public bool IncludeAncestors { get; init; }
}

/// <summary>Shared immutable level definitions. Undefined levels use the paragraph's list kind and start at one.</summary>
public sealed record ListDefinition
{
    public ImmutableArray<ListLevelDefinition> Levels { get; init; } = [];
    internal ListLevelDefinition Level(int level, ListKind kind) => level < Levels.Length ? Levels[level] :
        new() { Kind = kind, Marker = kind == ListKind.Bullet ? ListMarkerStyle.Bullet : ListMarkerStyle.Decimal };

    public bool Equals(ListDefinition? other) => other is not null && Levels.SequenceEqual(other.Levels);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var level in Levels) hash.Add(level); return hash.ToHashCode(); }

    internal void Validate()
    {
        if (Levels.IsDefault || Levels.Length > 9) throw new FormatException("Lists support at most nine levels.");
        foreach (var level in Levels)
            if (level is null || level.Start is < 1 or > 1_000_000 || !Enum.IsDefined(level.Kind) || level.Kind == ListKind.None ||
                !Enum.IsDefined(level.Marker) || level.Prefix is null || level.Suffix is null ||
                level.Prefix.Length > 100 || level.Suffix.Length > 100 || level.Text?.Length > 100)
                throw new FormatException("Invalid list level definition.");
    }
}

/// <summary>The visible marker and counter assigned to a paragraph in document order.</summary>
public sealed record ListMarker(string Text, int Number, int Level, Guid? ListId);

/// <summary>Document-wide numbering. Identified lists continue across ordinary paragraphs, sections, and cells.</summary>
public static class ListNumbering
{
    private sealed record CounterState(ListDefinition Definition, ImmutableArray<int?> Values)
    {
        public static CounterState Empty { get; } = new(new(), [null, null, null, null, null, null, null, null, null]);
        public bool Equals(CounterState? other) => other is not null && Definition.Equals(other.Definition) && Values.SequenceEqual(other.Values);
        public override int GetHashCode()
        {
            var hash = new HashCode(); hash.Add(Definition);
            foreach (var value in Values) hash.Add(value);
            return hash.ToHashCode();
        }
    }
    private readonly record struct EvaluationKey(Guid? Identity, CounterState Before);
    private sealed class EvaluationCache
    {
        // Bound variants retained by a shared subtree when earlier numbering changes repeatedly.
        private readonly Dictionary<EvaluationKey, CounterState> _entries = [];
        public CounterState Evaluate(EvaluationKey key, Func<CounterState> calculate)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var result)) return result;
                Interlocked.Increment(ref _evaluationMisses);
                result = calculate();
                if (_entries.Count >= 4) _entries.Clear();
                _entries.Add(key, result); return result;
            }
        }
    }
    private sealed class Result
    {
        public required IReadOnlyDictionary<Guid, ListMarker> Markers { get; init; }
    }
    private static long _evaluationMisses;
    internal static long EvaluationMisses => Interlocked.Read(ref _evaluationMisses);
    private static readonly ConditionalWeakTable<FlowDocument, Result> Cache = new();
    private static readonly ConditionalWeakTable<FlowDocument, FlowDocument> StyleProjections = new();
    private static FlowDocument ResolveStyles(FlowDocument document) => StyleProjections.GetValue(document, value =>
    {
        var resolver = new DocumentStyleResolver(value);
        return value.RewriteParagraphs(paragraph => [paragraph with { Style = resolver.ResolveParagraphStyle(paragraph.Style) }])
            with { Styles = new(), Defaults = new() };
    });
    private static readonly ConditionalWeakTable<StorageTree<OrderKey, DocumentNode>, EvaluationCache> Evaluations = new();

    /// <summary>Returns markers keyed by visible paragraph ID.</summary>
    public static IReadOnlyDictionary<Guid, ListMarker> Compute(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Cache.GetValue(document, d =>
        {
            var index = new DocumentIndex(d);
            if (!d.Styles.Paragraphs.IsEmpty || d.Defaults.Paragraph != ParagraphStyle.Default ||
                index.Enumerate(0, index.Length).Any(p => p.Paragraph.Style.Overrides is not null))
            { d = ResolveStyles(d); index = new DocumentIndex(d); }
            return new() { Markers = index.Enumerate(0, index.Length).Where(p => p.Paragraph.Style.List != ListKind.None)
                .ToImmutableDictionary(p => p.Paragraph.Id, p => GetMarker(d, p.Paragraph.Id)!) };
        }).Markers;
    }

    /// <summary>
    /// Gets a marker using cached immutable subtree transitions. Identified lists span containers;
    /// anonymous legacy lists count within their immediate container and reset on ordinary paragraphs.
    /// </summary>
    public static ListMarker? GetMarker(FlowDocument document, Guid paragraphId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var index = new DocumentIndex(document);
        var path = index.Tree.Paths?.Find(paragraphId)?.Value ?? throw new ArgumentException("The paragraph is not visible.", nameof(paragraphId));
        var paragraph = index.Tree.Locate(paragraphId).Node.Source as Paragraph ?? throw new ArgumentException("The identifier is not a paragraph.", nameof(paragraphId));
        if (!document.Styles.Paragraphs.IsEmpty || document.Defaults.Paragraph != ParagraphStyle.Default || paragraph.Style.Overrides is not null)
            return GetMarker(ResolveStyles(document), paragraphId);
        var style = paragraph.Style;
        if (style.List == ListKind.None) return null;
        var keys = path.Keys().ToArray();
        var parent = index.Tree.Root;
        var state = CounterState.Empty;
        for (var i = 0; i < keys.Length; i++)
        {
            if (style.ListId is not null || i == keys.Length - 1)
                state = EvaluateBefore(parent.Children, keys[i], style.ListId, state);
            if (i < keys.Length - 1) parent = parent.Children!.Find(keys[i])!.Value;
        }
        state = Advance(style, state);
        var format = state.Definition.Level(style.ListLevel, style.List);
        var number = state.Values[style.ListLevel]!.Value;
        var text = format.Kind == ListKind.Bullet || format.Marker == ListMarkerStyle.Bullet ? format.Text ?? "•" :
            format.Prefix + (format.IncludeAncestors ? string.Join(".", Enumerable.Range(0, style.ListLevel + 1)
                .Select(i => Format(state.Values[i]!.Value, state.Definition.Level(i, style.List).Marker))) : Format(number, format.Marker)) + format.Suffix;
        return new(text, number, style.ListLevel, style.ListId);
    }

    internal static void ValidateStyle(ParagraphStyle style)
    {
        if (style.ListId == Guid.Empty || style.ListStart is < 1 or > 1_000_000)
            throw new FormatException("List identifiers must be nonempty and starts must be between 1 and 1000000.");
        style.ListDefinition?.Validate();
    }

    private static CounterState EvaluateBefore(StorageTree<OrderKey, DocumentNode>? tree, OrderKey key, Guid? identity, CounterState state)
    {
        while (tree is not null)
        {
            if (key.CompareTo(tree.Key) <= 0) { tree = tree.Left; continue; }
            state = EvaluateTree(tree.Left, identity, state);
            state = EvaluateNode(tree.Value, identity, state);
            tree = tree.Right;
        }
        return state;
    }

    private static CounterState EvaluateTree(StorageTree<OrderKey, DocumentNode>? tree, Guid? identity, CounterState state)
    {
        if (tree is null) return state;
        return Evaluations.GetValue(tree, _ => new()).Evaluate(new(identity, state), () =>
        {
            var current = EvaluateTree(tree.Left, identity, state);
            current = EvaluateNode(tree.Value, identity, current);
            return EvaluateTree(tree.Right, identity, current);
        });
    }

    private static CounterState EvaluateNode(DocumentNode node, Guid? identity, CounterState state)
    {
        if (node.Source is Paragraph paragraph)
        {
            var style = paragraph.Style;
            if (identity is { } id) return style.ListId == id && style.List != ListKind.None ? Advance(style, state) : state;
            if (style.List == ListKind.None) return CounterState.Empty;
            // Legacy numbered sequences skip bullets, matching the v1 renderer.
            return style.List == ListKind.Numbered && style.ListId is null ? Advance(style, state) : state;
        }
        return identity is null ? state : EvaluateTree(node.Children, identity, state);
    }

    private static CounterState Advance(ParagraphStyle style, CounterState state)
    {
        var definition = style.ListDefinition ?? state.Definition;
        var values = state.Values.ToBuilder();
        var level = style.ListLevel;
        var format = definition.Level(level, style.List);
        for (var i = 0; i < level; i++) values[i] ??= definition.Level(i, style.List).Start;
        values[level] = style.ListStart ?? (style.ListRestart || values[level] is null ? format.Start : values[level]!.Value + 1);
        for (var i = level + 1; i < values.Count; i++) values[i] = null;
        return new(definition, values.ToImmutable());
    }
    internal static string Format(int value, ListMarkerStyle marker)
    {
        if (marker is ListMarkerStyle.LowerLetter or ListMarkerStyle.UpperLetter)
        {
            var result = "";
            while (value > 0) { value--; result = (char)('A' + value % 26) + result; value /= 26; }
            return marker == ListMarkerStyle.LowerLetter ? result.ToLowerInvariant() : result;
        }
        if ((marker is ListMarkerStyle.LowerRoman or ListMarkerStyle.UpperRoman) && value <= 3999)
        {
            var result = "";
            (int Value, string Text)[] digits = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"),
                (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
            foreach (var digit in digits) while (value >= digit.Value) { result += digit.Text; value -= digit.Value; }
            return marker == ListMarkerStyle.LowerRoman ? result.ToLowerInvariant() : result;
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
