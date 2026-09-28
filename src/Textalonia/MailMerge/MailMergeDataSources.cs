using System.Collections.ObjectModel;

namespace Textalonia.MailMerge;

/// <summary>A scalar field exposed by a host's recipient source.</summary>
public sealed record MailMergeFieldSchema(string Name, Type ValueType);

/// <summary>The scalar fields and named child collections available at one merge level.</summary>
public sealed class MailMergeSchema
{
    /// <summary>Scalar fields at this level, in discovery or host-defined order.</summary>
    public IReadOnlyList<MailMergeFieldSchema> Fields { get; }

    /// <summary>Named child collection schemas. Names are matched ordinally.</summary>
    public IReadOnlyDictionary<string, MailMergeSchema> Children { get; }

    /// <summary>Creates an explicit schema, including schemas for any nested regions.</summary>
    public MailMergeSchema(IEnumerable<MailMergeFieldSchema> fields,
        IReadOnlyDictionary<string, MailMergeSchema>? children = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var fieldList = new List<MailMergeFieldSchema>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateName(field.Name);
            ArgumentNullException.ThrowIfNull(field.ValueType);
            if (!names.Add(field.Name)) throw new ArgumentException($"Duplicate merge field '{field.Name}'.", nameof(fields));
            fieldList.Add(field);
        }
        Fields = fieldList.AsReadOnly();
        var childMap = new Dictionary<string, MailMergeSchema>(StringComparer.Ordinal);
        if (children is not null)
            foreach (var pair in children)
            {
                ValidateName(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                if (!childMap.TryAdd(pair.Key, pair.Value))
                    throw new ArgumentException($"Duplicate child collection '{pair.Key}'.", nameof(children));
            }
        Children = new ReadOnlyDictionary<string, MailMergeSchema>(childMap);
    }

    /// <summary>Discovers fields and re-readable child collections in a finite dictionary collection.</summary>
    /// <remarks>For a lazy or remote source, the host should supply a schema without enumerating records.</remarks>
    public static MailMergeSchema Discover(IEnumerable<IReadOnlyDictionary<string, object?>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Discover(records, 0);
    }

    private static MailMergeSchema Discover(IEnumerable<IReadOnlyDictionary<string, object?>> records, int depth)
    {
        if (depth > 32) throw new ArgumentException("Merge schema nesting exceeds 32 levels.", nameof(records));
        var fields = new Dictionary<string, HashSet<Type>>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            foreach (var pair in record)
            {
                ValidateName(pair.Key);
                if (pair.Value is IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
                {
                    if (fields.ContainsKey(pair.Key)) throw new ArgumentException($"'{pair.Key}' is both a scalar field and a child collection.", nameof(records));
                    if (!children.TryGetValue(pair.Key, out var collected)) children.Add(pair.Key, collected = []);
                    foreach (var row in rows) collected.Add(row);
                }
                else
                {
                    if (children.ContainsKey(pair.Key)) throw new ArgumentException($"'{pair.Key}' is both a scalar field and a child collection.", nameof(records));
                    if (!fields.TryGetValue(pair.Key, out var types)) fields.Add(pair.Key, types = []);
                    if (pair.Value is not null) types.Add(pair.Value.GetType());
                }
            }
        }
        return new MailMergeSchema(fields.Select(pair => new MailMergeFieldSchema(pair.Key,
            pair.Value.Count == 1 ? pair.Value.Single() : typeof(object))),
            children.ToDictionary(pair => pair.Key, pair => Discover(pair.Value, depth + 1), StringComparer.Ordinal));
    }

    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            throw new ArgumentException("Merge names must contain 1 to 256 non-control characters and cannot be blank.", nameof(name));
    }
}

/// <summary>A recipient or child row with scalar values and explicitly supplied child collections.</summary>
public sealed class MailMergeRecord
{
    /// <summary>Scalar values used by MERGEFIELD and field evaluation.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Named child collections used by repeating regions.</summary>
    public IReadOnlyDictionary<string, IEnumerable<MailMergeRecord>> Children { get; }

    /// <summary>Creates a record without traversing or loading its child collections.</summary>
    public MailMergeRecord(IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, IEnumerable<MailMergeRecord>>? children = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var valueMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            MailMergeSchema.ValidateName(pair.Key);
            if (!valueMap.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException($"Duplicate merge field '{pair.Key}'.", nameof(values));
        }
        Values = new ReadOnlyDictionary<string, object?>(valueMap);
        var childMap = new Dictionary<string, IEnumerable<MailMergeRecord>>(StringComparer.Ordinal);
        if (children is not null)
            foreach (var pair in children)
            {
                MailMergeSchema.ValidateName(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                if (!childMap.TryAdd(pair.Key, pair.Value))
                    throw new ArgumentException($"Duplicate child collection '{pair.Key}'.", nameof(children));
            }
        Children = new ReadOnlyDictionary<string, IEnumerable<MailMergeRecord>>(childMap);
    }

    /// <summary>Gets a named child collection; an absent collection has zero rows.</summary>
    public IEnumerable<MailMergeRecord> GetChildren(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Children.TryGetValue(name, out var children) ? children : [];
    }

    /// <summary>Projects this record for dictionary-based merge APIs, with lazy child collections by name.</summary>
    public IReadOnlyDictionary<string, object?> ToMergeValues()
    {
        var values = Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var child in Children)
        {
            if (values.ContainsKey(child.Key))
                throw new InvalidOperationException($"'{child.Key}' is both a scalar field and a child collection.");
            values.Add(child.Key, Project(child.Value));
        }
        return values;

        static IEnumerable<IReadOnlyDictionary<string, object?>> Project(IEnumerable<MailMergeRecord> records)
        {
            foreach (var record in records)
            {
                ArgumentNullException.ThrowIfNull(record);
                yield return record.ToMergeValues();
            }
        }
    }
}

/// <summary>A source whose schema and records are supplied by the host application.</summary>
/// <remarks>Textalonia never resolves connection information from imported documents.</remarks>
public interface IMailMergeDataSource
{
    /// <summary>Describes available fields and nested collections without reading recipients.</summary>
    MailMergeSchema Schema { get; }

    /// <summary>Enumerates recipients on demand. Implementations should observe cancellation.</summary>
    IEnumerable<MailMergeRecord> GetRecipients(CancellationToken cancellationToken = default);
}

/// <summary>Wraps an existing finite collection of ordinary dictionary recipients.</summary>
public sealed class DictionaryMailMergeDataSource : IMailMergeDataSource
{
    private readonly IEnumerable<IReadOnlyDictionary<string, object?>> _records;

    /// <summary>Discovers a schema from a finite in-memory collection.</summary>
    public DictionaryMailMergeDataSource(IReadOnlyList<IReadOnlyDictionary<string, object?>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
        Schema = MailMergeSchema.Discover(records);
    }

    /// <summary>Uses a supplied schema so a lazy dictionary source remains lazy.</summary>
    public DictionaryMailMergeDataSource(IEnumerable<IReadOnlyDictionary<string, object?>> records, MailMergeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(schema);
        _records = records;
        Schema = schema;
    }

    /// <inheritdoc />
    public MailMergeSchema Schema { get; }

    /// <inheritdoc />
    public IEnumerable<MailMergeRecord> GetRecipients(CancellationToken cancellationToken = default)
    {
        foreach (var values in _records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MailMergeRecord(values);
        }
    }
}

/// <summary>Adapts a host-owned record factory, including hierarchical records.</summary>
public sealed class DelegateMailMergeDataSource : IMailMergeDataSource
{
    private readonly Func<CancellationToken, IEnumerable<MailMergeRecord>> _getRecipients;

    /// <summary>Creates a data source without opening or retaining an external connection.</summary>
    public DelegateMailMergeDataSource(MailMergeSchema schema,
        Func<CancellationToken, IEnumerable<MailMergeRecord>> getRecipients)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _getRecipients = getRecipients ?? throw new ArgumentNullException(nameof(getRecipients));
    }

    /// <inheritdoc />
    public MailMergeSchema Schema { get; }

    /// <inheritdoc />
    public IEnumerable<MailMergeRecord> GetRecipients(CancellationToken cancellationToken = default) =>
        _getRecipients(cancellationToken) ?? throw new InvalidOperationException("The recipient factory returned null.");
}

/// <summary>Explicit recipient selection, filtering and sorting for a merge batch.</summary>
public sealed record MailMergeRecipientSelection
{
    /// <summary>Optional zero-based indexes in original source order.</summary>
    public IReadOnlyCollection<int>? SourceIndexes { get; init; }

    /// <summary>Optional host predicate applied after index selection.</summary>
    public Func<MailMergeRecord, bool>? Filter { get; init; }

    /// <summary>Optional comparer. Sorting buffers the selected recipients; no sort streams them.</summary>
    public IComparer<MailMergeRecord>? SortComparer { get; init; }
}

/// <summary>Applies a recipient selection to a host data source.</summary>
public static class MailMergeDataSources
{
    /// <summary>Returns selected records lazily unless sorting was requested.</summary>
    public static IEnumerable<MailMergeRecord> SelectRecipients(IMailMergeDataSource source,
        MailMergeRecipientSelection? selection = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Schema);
        var indexes = selection?.SourceIndexes is { } requested ? new HashSet<int>(requested) : null;
        if (indexes?.Any(index => index < 0) == true)
            throw new ArgumentOutOfRangeException(nameof(selection), "Recipient indexes must be nonnegative.");
        return Enumerate();

        IEnumerable<MailMergeRecord> Enumerate()
        {
            IEnumerable<MailMergeRecord> Filtered()
            {
                var index = 0;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var recipient in source.GetRecipients(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(recipient);
                    var selected = indexes is null || indexes.Contains(index);
                    checked { index++; }
                    if (selected && (selection?.Filter is null || selection.Filter(recipient))) yield return recipient;
                }
            }

            if (selection?.SortComparer is { } comparer)
            {
                // LINQ OrderBy is stable when the comparer considers two records equal.
                foreach (var recipient in Filtered().OrderBy(record => record, comparer))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return recipient;
                }
            }
            else
                foreach (var recipient in Filtered()) yield return recipient;
        }
    }
}
