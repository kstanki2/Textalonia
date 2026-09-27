using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>Imports fragment catalogs without replacing or changing destination definitions.</summary>
internal static class DocumentStyleImport
{
    internal static FlowDocument Prepare(FlowDocument source, FlowDocument destination)
    {
        if (destination.Blocks is [Paragraph { Length: 0 }] && destination.Styles.Characters.IsEmpty &&
            destination.Styles.Paragraphs.IsEmpty && destination.Styles.Tables.IsEmpty)
            return source;
        // Physical defaults are document-wide. A foreign context is materialized at the
        // clipboard boundary so its appearance cannot change the destination document.
        var empty = new Paragraph { Style = ParagraphStyle.ForStyle(null), DefaultStyle = TextStyle.ForStyle(null) };
        var sourceResolver = new DocumentStyleResolver(source);
        var targetResolver = new DocumentStyleResolver(destination);
        if (source.Defaults != destination.Defaults || !SameTheme(source.Theme, destination.Theme) ||
            sourceResolver.ResolveParagraphStyle(empty.Style) != targetResolver.ResolveParagraphStyle(empty.Style) ||
            sourceResolver.ResolveText(empty, empty.DefaultStyle) != targetResolver.ResolveText(empty, empty.DefaultStyle))
            source = sourceResolver.ResolveDocument();

        var paragraphs = Map(source.Styles.Paragraphs, destination.Styles.Paragraphs);
        var characters = Map(source.Styles.Characters, destination.Styles.Characters);
        var tables = Map(source.Styles.Tables, destination.Styles.Tables);
        static string? Id(Dictionary<string, string> map, string? id) => id is null ? null : map[id];
        static bool Moved(Dictionary<string, string> map, string? id) => id is not null && map[id] != id;
        var occupiedIds = destination.Styles.Paragraphs.Keys.Concat(destination.Styles.Characters.Keys)
            .Concat(destination.Styles.Tables.Keys).Concat(paragraphs.Keys).Concat(characters.Keys).Concat(tables.Keys)
            .Concat(paragraphs.Values).Concat(characters.Values).Concat(tables.Values).ToHashSet(StringComparer.Ordinal);
        bool Remap(Dictionary<string, string> map, string id)
        {
            if (map[id] != id) return false;
            var suffix = 1; string replacement;
            do { replacement = id[..Math.Min(id.Length, 220)] + "-copy-" + suffix++; } while (!occupiedIds.Add(replacement));
            map[id] = replacement;
            return true;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var style in source.Styles.Paragraphs.Values)
                if (destination.Styles.Paragraphs.ContainsKey(style.Id) &&
                    (Moved(paragraphs, style.BasedOn) || Moved(paragraphs, style.NextStyle) || Moved(characters, style.LinkedStyle)))
                    changed |= Remap(paragraphs, style.Id);
            foreach (var style in source.Styles.Characters.Values)
                if (destination.Styles.Characters.ContainsKey(style.Id) &&
                    (Moved(characters, style.BasedOn) || Moved(paragraphs, style.LinkedStyle)))
                    changed |= Remap(characters, style.Id);
            foreach (var style in source.Styles.Tables.Values)
                if (destination.Styles.Tables.ContainsKey(style.Id) && Moved(tables, style.BasedOn))
                    changed |= Remap(tables, style.Id);
        } while (changed);
        var catalog = destination.Styles with
        {
            Paragraphs = destination.Styles.Paragraphs.SetItems(source.Styles.Paragraphs.Values.Select(style =>
                new KeyValuePair<string, ParagraphStyleDefinition>(paragraphs[style.Id], style with
                {
                    Id = paragraphs[style.Id], BasedOn = Id(paragraphs, style.BasedOn),
                    LinkedStyle = Id(characters, style.LinkedStyle), NextStyle = Id(paragraphs, style.NextStyle)
                }))),
            Characters = destination.Styles.Characters.SetItems(source.Styles.Characters.Values.Select(style =>
                new KeyValuePair<string, CharacterStyleDefinition>(characters[style.Id], style with
                {
                    Id = characters[style.Id], BasedOn = Id(characters, style.BasedOn), LinkedStyle = Id(paragraphs, style.LinkedStyle)
                }))),
            Tables = destination.Styles.Tables.SetItems(source.Styles.Tables.Values.Select(style =>
                new KeyValuePair<string, TableStyleDefinition>(tables[style.Id], style with
                { Id = tables[style.Id], BasedOn = Id(tables, style.BasedOn) })))
        };
        TextStyle Text(TextStyle style) => style with { StyleId = Id(characters, style.StyleId ??
            (style.Overrides is not null ? source.Styles.DefaultCharacterStyleId : null)) };
        ImmutableArray<Block> Visit(ImmutableArray<Block> blocks) => blocks.Select<Block, Block>(block => block switch
        {
            Paragraph p => p with
            {
                Style = p.Style with { StyleId = Id(paragraphs, p.Style.StyleId ??
                    (p.Style.Overrides is not null ? source.Styles.DefaultParagraphStyleId : null)) },
                DefaultStyle = Text(p.DefaultStyle), Runs = p.Runs.Select(run => run with { Style = Text(run.Style) }).ToImmutableArray()
            },
            Section section => section with { Blocks = Visit(section.Blocks) },
            Table table => table with
            {
                StyleId = Id(tables, table.StyleId ?? source.Styles.DefaultTableStyleId),
                Rows = table.Rows.Select(row => row.Select(cell => cell with
                { Blocks = Visit(cell.Blocks), MergeOriginalBlocks = Visit(cell.MergeOriginalBlocks) }).ToImmutableArray()).ToImmutableArray()
            },
            _ => block
        }).ToImmutableArray();
        return source with { Blocks = Visit(source.Blocks), Styles = catalog, Defaults = destination.Defaults, Theme = destination.Theme };
    }

    private static bool SameTheme(DocumentTheme a, DocumentTheme b) =>
        a.Colors.Count == b.Colors.Count && a.Fonts.Count == b.Fonts.Count &&
        a.Colors.All(pair => b.Colors.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
        a.Fonts.All(pair => b.Fonts.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static Dictionary<string, string> Map<T>(ImmutableDictionary<string, T> source, ImmutableDictionary<string, T> destination)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupied = source.Keys.Concat(destination.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            var id = pair.Key;
            if (destination.TryGetValue(id, out var existing) && !EqualityComparer<T>.Default.Equals(existing, pair.Value))
            {
                var prefix = id[..Math.Min(id.Length, 220)];
                var suffix = 1;
                do { id = prefix + "-copy-" + suffix++; } while (!occupied.Add(id));
            }
            result.Add(pair.Key, id);
        }
        return result;
    }
}

