using System.Collections.Immutable;
using System.Globalization;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.MailMerge;

/// <summary>Fills typed merge fields without changing the template or executing field instructions.</summary>
/// <remarks>
/// Names match ordinally and case-sensitively, regardless of the record dictionary's comparer.
/// Values may be strings, characters, booleans, or <see cref="IFormattable"/> instances.
/// Null values use the field fallback or empty text. Each resolved value is limited to 16,384
/// UTF-16 characters and cannot contain NUL; standard numeric format precision is limited to 1,024 digits.
/// Newlines become soft line breaks. Secondary stories, hidden table cells and retained merge backups are included.
/// </remarks>
public static class MailMergeProcessor
{
    private const int MaximumValueLength = 16_384;
    private const int MaximumFormatPrecision = 1_024;

    /// <summary>Returns distinct field names in first occurrence order, including retained table content.</summary>
    public static ImmutableArray<string> GetFieldNames(FlowDocument template)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Validate();
        var names = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(ImmutableArray<Block> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var run in paragraph.Runs)
                            if (run.Inline?.Payload is MergeFieldInlinePayload field && seen.Add(field.Name))
                                names.Add(field.Name);
                        break;
                    case Section section: Visit(section.Blocks); break;
                    case Table table:
                        foreach (var row in table.Rows)
                            foreach (var cell in row)
                            { Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); }
                        break;
                }
        }
        Visit(template.Blocks);
        foreach (var story in template.Stories.OrderBy(pair => pair.Key)) Visit(story.Value.Blocks);
        return names.ToImmutable();
    }

    /// <summary>Replaces resolved fields with ordinary styled text in a new immutable snapshot.</summary>
    public static FlowDocument Merge(FlowDocument template, IReadOnlyDictionary<string, object?> data,
        MailMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var settings = Prepare(template, options, cancellationToken);
        return Process(template, data, settings, false, cancellationToken);
    }

    /// <summary>Updates field display text while retaining definitions and IDs, so another record can be previewed.</summary>
    public static FlowDocument Preview(FlowDocument template, IReadOnlyDictionary<string, object?> data,
        MailMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var settings = Prepare(template, options, cancellationToken);
        return Process(template, data, settings, true, cancellationToken);
    }

    /// <summary>Lazily generates one independent document per record, in enumeration order.</summary>
    /// <remarks>
    /// Template and options are validated immediately. Records are read and copied only as requested.
    /// Enumeration stops at the first invalid record or cancellation; dispose the enumerator when stopping early.
    /// IDs are preserved within each output snapshot, as with <see cref="Merge"/>.
    /// </remarks>
    public static IEnumerable<FlowDocument> MergeMany(FlowDocument template,
        IEnumerable<IReadOnlyDictionary<string, object?>> records, MailMergeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var settings = Prepare(template, options, cancellationToken);
        return Enumerate();

        IEnumerable<FlowDocument> Enumerate()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = records.GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) yield break;
                cancellationToken.ThrowIfCancellationRequested();
                var data = enumerator.Current;
                ArgumentNullException.ThrowIfNull(data);
                yield return Process(template, data, settings, false, cancellationToken);
            }
        }
    }

    private static MailMergeOptions Prepare(FlowDocument template, MailMergeOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(template);
        token.ThrowIfCancellationRequested();
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Culture);
        if (!Enum.IsDefined(options.MissingFieldBehavior))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown missing-field behavior.");
        template.Validate();
        token.ThrowIfCancellationRequested();
        return options with { Culture = CultureInfo.ReadOnly((CultureInfo)options.Culture.Clone()) };
    }

    private static FlowDocument Process(FlowDocument template, IReadOnlyDictionary<string, object?> source,
        MailMergeOptions options, bool preview, CancellationToken token)
    {
        // Copy once so the record's comparer cannot silently change name matching semantics.
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            token.ThrowIfCancellationRequested();
            data.Add(pair.Key, pair.Value);
        }

        ImmutableArray<Block> RewriteBlocks(ImmutableArray<Block> blocks)
        {
            ImmutableArray<Block>.Builder? changed = null;
            for (var i = 0; i < blocks.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var original = blocks[i];
                var replacement = RewriteBlock(original);
                if (ReferenceEquals(original, replacement)) continue;
                changed ??= blocks.ToBuilder();
                changed[i] = replacement;
            }
            return changed?.ToImmutable() ?? blocks;
        }

        Block RewriteBlock(Block block)
        {
            switch (block)
            {
                case Paragraph paragraph:
                {
                    ImmutableArray<RichRun>.Builder? changed = null;
                    for (var i = 0; i < paragraph.Runs.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var run = paragraph.Runs[i];
                        if (run.Inline is not { Payload: MergeFieldInlinePayload field } inline) continue;
                        var value = Resolve(field, data, options);
                        token.ThrowIfCancellationRequested();
                        if (value is null || preview && value == inline.AltText) continue;
                        changed ??= paragraph.Runs.ToBuilder();
                        changed[i] = preview
                            ? new RichRun(inline with { AltText = value }, run.Style)
                            : new RichRun(value, run.Style);
                    }
                    return changed is null ? paragraph : paragraph with { Runs = Paragraph.Normalize(changed) };
                }
                case Section section:
                {
                    var blocks = RewriteBlocks(section.Blocks);
                    return blocks == section.Blocks ? section : section with { Blocks = blocks };
                }
                case Table table:
                {
                    ImmutableArray<ImmutableArray<TableCell>>.Builder? rows = null;
                    for (var r = 0; r < table.Rows.Length; r++)
                    {
                        ImmutableArray<TableCell>.Builder? row = null;
                        for (var c = 0; c < table.Rows[r].Length; c++)
                        {
                            token.ThrowIfCancellationRequested();
                            var cell = table.Rows[r][c];
                            var blocks = RewriteBlocks(cell.Blocks);
                            var backup = RewriteBlocks(cell.MergeOriginalBlocks);
                            if (blocks == cell.Blocks && backup == cell.MergeOriginalBlocks) continue;
                            row ??= table.Rows[r].ToBuilder();
                            row[c] = cell with { Blocks = blocks, MergeOriginalBlocks = backup };
                        }
                        if (row is null) continue;
                        rows ??= table.Rows.ToBuilder();
                        rows[r] = row.ToImmutable();
                    }
                    return rows is null ? table : table with { Rows = rows.ToImmutable() };
                }
                default: throw new NotSupportedException("Unknown document block type.");
            }
        }

        var blocks = RewriteBlocks(template.Blocks);
        var stories = template.Stories;
        foreach (var pair in template.Stories)
        {
            var storyBlocks = RewriteBlocks(pair.Value.Blocks);
            if (storyBlocks != pair.Value.Blocks) stories = stories.SetItem(pair.Key, pair.Value with { Blocks = storyBlocks });
        }
        token.ThrowIfCancellationRequested();
        var result = blocks == template.Blocks && ReferenceEquals(stories, template.Stories) ? template : template with { Blocks = blocks, Stories = stories };
        result = DocumentAnchors.Reconcile(template, result);
        if (options.FieldOptions is { } fieldOptions)
        {
            var evaluated = FieldEvaluator.Update(result, fieldOptions with { MergeValues = data, Culture = options.Culture, CancellationToken = token });
            foreach (var diagnostic in evaluated.Diagnostics) options.FieldDiagnostic?.Invoke(diagnostic);
            result = evaluated.Document;
        }
        return result;
    }

    // A null result means retain the live field. An empty string is a resolved value.
    private static string? Resolve(MergeFieldInlinePayload field, Dictionary<string, object?> data, MailMergeOptions options)
    {
        string text;
        if (!data.TryGetValue(field.Name, out var value))
        {
            if (field.FallbackText is { } fallback) text = fallback;
            else if (options.MissingFieldBehavior == MissingFieldBehavior.KeepField) return null;
            else if (options.MissingFieldBehavior == MissingFieldBehavior.Empty) text = "";
            else throw new KeyNotFoundException($"No value was supplied for merge field '{field.Name}'.");
        }
        else if (value is null) text = field.FallbackText ?? "";
        else
        {
            ValidatePrecision(field);
            if (!string.IsNullOrEmpty(field.Format) && value is not IFormattable)
                throw new FormatException($"The value for merge field '{field.Name}' does not support a format string.");
            text = value switch
            {
                string literal => literal,
                char character => character.ToString(),
                bool boolean => boolean.ToString(),
                IFormattable formattable => formattable.ToString(field.Format, options.Culture) ?? "",
                _ => throw new ArgumentException($"The value for merge field '{field.Name}' must be a string, character, boolean, or IFormattable.", nameof(data))
            };
        }
        if (text.Length > MaximumValueLength)
            throw new FormatException($"The value for merge field '{field.Name}' exceeds {MaximumValueLength} characters.");
        if (text.Contains('\0'))
            throw new FormatException($"The value for merge field '{field.Name}' cannot contain NUL.");
        return FlowDocument.NormalizeNewlines(text).Replace('\n', '\u2028');
    }

    private static void ValidatePrecision(MergeFieldInlinePayload field)
    {
        if (field.Format is not { Length: > 1 } format || !char.IsAsciiLetter(format[0])) return;
        // Standard formats accept a digit precision. Reject excessive precision before .NET allocates output.
        if (!format.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
        {
            var precision = 0;
            foreach (var digit in format.AsSpan(1))
            {
                precision = precision * 10 + digit - '0';
                if (precision > MaximumFormatPrecision)
                    throw new FormatException($"The format precision for merge field '{field.Name}' exceeds {MaximumFormatPrecision}.");
            }
        }
    }
}
