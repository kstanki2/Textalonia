using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using System.ComponentModel;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class LoadBaselineTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Text_binding_survives_edits_and_host_replacement() => fixture.Session.Dispatch(() =>
    {
        var model = new TextModel { Text = "original" };
        var editor = new TextaloniaEditor { DataContext = model };
        editor.Bind(TextaloniaEditor.TextProperty, new Binding(nameof(TextModel.Text)) { Mode = BindingMode.TwoWay });
        editor.Session.SelectAll(); editor.InsertText("changed");
        Assert.Equal("changed", model.Text);
        model.Text = "replacement\r\ntext";
        Assert.Equal("replacement\ntext", editor.Text);
        editor.InsertText("X");
        Assert.Equal(editor.Text, model.Text);
    }, CancellationToken.None);

    [Fact]
    public Task Concurrent_loads_accept_first_completion_and_reject_stale_results() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "before" };
        var slow = new ControlledFormat();
        var fast = new ControlledFormat();
        using var a = new MemoryStream(); using var b = new MemoryStream();
        var first = editor.LoadAsync(a, slow);
        var second = editor.LoadAsync(b, fast);
        fast.Complete("second"); await second;
        slow.Complete("first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal("second", editor.Text);
        Assert.True(a.CanRead && b.CanRead);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Editing_during_load_rejects_result_but_selection_changes_allow_it() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "before" };
        using var stream = new MemoryStream();
        var format = new ControlledFormat();
        var pending = editor.LoadAsync(stream, format);
        editor.InsertText("X");
        format.Complete("loaded");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Xbefore", editor.Text);
        format = new ControlledFormat();
        pending = editor.LoadAsync(stream, format);
        editor.Session.SelectAll();
        format.Complete("accepted"); await pending;
        Assert.Equal("accepted", editor.Text);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Reporting_load_rejects_stale_results_before_publishing_or_replacing() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "before" };
        var reports = 0;
        editor.ConversionCompleted += (_, _) => reports++;
        using var stream = new MemoryStream();
        var format = new ControlledFormat();
        var pending = editor.LoadWithReportAsync(stream, format);
        editor.InsertText("X");
        format.Complete("stale");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Xbefore", editor.Text);
        Assert.Empty(editor.LastConversionReport.Diagnostics);
        Assert.Equal(0, reports);
        format = new ControlledFormat();
        pending = editor.LoadWithReportAsync(stream, format);
        format.Complete("accepted");
        var result = await pending;
        Assert.Equal("accepted", editor.Text);
        Assert.Same(result.Report, editor.LastConversionReport);
        Assert.Equal(1, reports);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "conversion.diagnostics-unavailable");
        return true;
    }, CancellationToken.None);
    private sealed class ControlledFormat : IDocumentFormat
    {
        private readonly TaskCompletionSource<FlowDocument> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "controlled baseline";
        public IReadOnlyList<string> Extensions => [];
        public void Complete(string text) => _completion.SetResult(FlowDocument.FromText(text));
        public Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default) => _completion.Task.WaitAsync(cancellationToken);
        public Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TextModel : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
