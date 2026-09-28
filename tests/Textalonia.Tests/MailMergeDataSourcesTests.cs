using Textalonia.MailMerge;
using Xunit;

namespace Textalonia.Tests;

public class MailMergeDataSourcesTests
{
    [Fact]
    public void Dictionary_source_discovers_union_of_scalar_fields_with_ordinal_names()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records =
        [
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Amount"] = null },
            new Dictionary<string, object?> { ["name"] = "Grace", ["Amount"] = 12m },
            new Dictionary<string, object?> { ["Amount"] = 13.5d }
        ];

        var source = new DictionaryMailMergeDataSource(records);

        Assert.Equal(new[] { "Name", "Amount", "name" }, source.Schema.Fields.Select(field => field.Name));
        Assert.Equal(typeof(string), source.Schema.Fields[0].ValueType);
        Assert.Equal(typeof(object), source.Schema.Fields[1].ValueType);
        Assert.Empty(source.Schema.Children);
        Assert.Equal(3, source.GetRecipients().Count());
    }

    [Fact]
    public void Dictionary_source_discovers_nested_re_readable_child_collections()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> taxes =
            [new Dictionary<string, object?> { ["Rate"] = 0.1m }];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> details =
            [new Dictionary<string, object?> { ["Item"] = "Pencil", ["Taxes"] = taxes }];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> recipients =
            [new Dictionary<string, object?> { ["Customer"] = "Ada", ["Details"] = details }];

        var schema = new DictionaryMailMergeDataSource(recipients).Schema;

        Assert.Equal("Customer", Assert.Single(schema.Fields).Name);
        Assert.Equal("Item", Assert.Single(schema.Children["Details"].Fields).Name);
        Assert.Equal(typeof(decimal), Assert.Single(schema.Children["Details"].Children["Taxes"].Fields).ValueType);
    }

    [Fact]
    public void Explicit_schema_keeps_dictionary_source_lazy_and_selection_uses_original_indexes()
    {
        var read = 0;
        IEnumerable<IReadOnlyDictionary<string, object?>> Records()
        {
            for (var index = 0; index < 4; index++)
            {
                read++;
                yield return new Dictionary<string, object?> { ["Index"] = index };
            }
        }
        var source = new DictionaryMailMergeDataSource(Records(),
            new MailMergeSchema([new("Index", typeof(int))]));
        var selection = new MailMergeRecipientSelection
        {
            SourceIndexes = [1, 2, 3],
            Filter = record => (int)record.Values["Index"]! != 2
        };

        var query = MailMergeDataSources.SelectRecipients(source, selection);
        Assert.Equal(0, read);
        using var enumerator = query.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current.Values["Index"]);
        Assert.Equal(2, read);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(3, enumerator.Current.Values["Index"]);
        Assert.Equal(4, read);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void Sorting_is_stable_and_only_buffers_when_requested()
    {
        var schema = new MailMergeSchema([new("Rank", typeof(int)), new("Name", typeof(string))]);
        var source = new DelegateMailMergeDataSource(schema, _ =>
        [
            new(new Dictionary<string, object?> { ["Rank"] = 2, ["Name"] = "second" }),
            new(new Dictionary<string, object?> { ["Rank"] = 1, ["Name"] = "first" }),
            new(new Dictionary<string, object?> { ["Rank"] = 2, ["Name"] = "third" })
        ]);
        var selection = new MailMergeRecipientSelection
        {
            SortComparer = Comparer<MailMergeRecord>.Create((left, right) =>
                ((int)left.Values["Rank"]!).CompareTo((int)right.Values["Rank"]!))
        };

        Assert.Equal(new[] { "first", "second", "third" },
            MailMergeDataSources.SelectRecipients(source, selection).Select(record => record.Values["Name"]));
    }

    [Fact]
    public void Child_collections_project_as_lazy_nested_dictionary_values()
    {
        var childReads = 0;
        IEnumerable<MailMergeRecord> Details()
        {
            childReads++;
            yield return new MailMergeRecord(new Dictionary<string, object?> { ["Item"] = "Pencil" },
                new Dictionary<string, IEnumerable<MailMergeRecord>>
                {
                    ["Taxes"] = [new(new Dictionary<string, object?> { ["Rate"] = 0.1m })]
                });
        }
        var root = new MailMergeRecord(new Dictionary<string, object?> { ["Customer"] = "Ada" },
            new Dictionary<string, IEnumerable<MailMergeRecord>> { ["Details"] = Details() });

        var values = root.ToMergeValues();
        Assert.Equal(0, childReads);
        var details = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(values["Details"]);
        var detail = Assert.Single(details);
        Assert.Equal(1, childReads);
        Assert.Equal("Pencil", detail["Item"]);
        var taxes = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(detail["Taxes"]);
        Assert.Equal(0.1m, Assert.Single(taxes)["Rate"]);
        Assert.Empty(root.GetChildren("Missing"));
    }

    [Fact]
    public void Invalid_indexes_are_rejected_before_source_is_enumerated_and_cancellation_stops_it()
    {
        var calls = 0;
        var source = new DelegateMailMergeDataSource(new MailMergeSchema([]), _ =>
        {
            calls++;
            return [new(new Dictionary<string, object?>())];
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => MailMergeDataSources.SelectRecipients(source,
            new MailMergeRecipientSelection { SourceIndexes = [-1] }));
        Assert.Equal(0, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MailMergeDataSources.SelectRecipients(source,
            cancellationToken: cancellation.Token).ToArray());
        Assert.Equal(0, calls);
    }
}
