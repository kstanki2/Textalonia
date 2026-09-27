using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Textalonia.Controls;

namespace Textalonia.Benchmarks;

/// <summary>Measures the complete source-to-frame path while independently probing the UI dispatcher.</summary>
internal static class MarkdownProbe
{
    private const int Warmups = 3;
    private const int Repetitions = 40;
    private const double UpdateBudgetMilliseconds = 100;
    private const double DispatchBudgetMilliseconds = 16;
    private sealed record Sample(double Milliseconds, long AllocatedBytes, int SourceLength, double ScrollDrift,
        bool SelectionPreserved, List<double> UiDispatchMilliseconds);
    private sealed record Result(int InitialParagraphs, string Operation, double BudgetMilliseconds,
        double MedianMilliseconds, double P95Milliseconds, bool WithinLatencyBudget, double DispatchBudgetMilliseconds,
        double DispatchP95Milliseconds, bool WithinDispatchBudget, int DispatchSamples, long P95AllocatedBytes,
        double MaximumScrollDrift, List<Sample> Samples);

    public static void Run(string destination)
    {
        Directory.CreateDirectory(destination);
        var results = new List<Result>();
        var bursts = new List<object>();
        using var ui = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        ui.Dispatch(async () =>
        {
            foreach (var paragraphs in new[] { 100, 1000 })
            {
                var viewer = new MarkdownViewer { FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter") };
                var window = new Window { Width = 800, Height = 500, Content = viewer };
                try
                {
                    window.Show(); window.UpdateLayout();
                    var source = CreateSource(paragraphs);
                    await viewer.UpdateMarkdownAsync(source);
                    Frame(window, viewer);
                    viewer.Session.Select(12, 18);
                    Frame(window, viewer);
                    viewer.Scroller!.Offset = new Vector(0, 320);
                    Frame(window, viewer);
                    for (var settle = 0; settle < 3; settle++) Frame(window, viewer);
                    var selection = viewer.Session.Selection;
                    var offset = viewer.Scroller.Offset;

                    async Task Measure(string operation, Func<int, Task> update, Func<int, string> expectedMarker)
                    {
                        var samples = new List<Sample>();
                        for (var i = -Warmups; i < Repetitions; i++)
                        {
                            var dispatchTimes = new ConcurrentQueue<double>();
                            using var stopHeartbeat = new CancellationTokenSource();
                            var heartbeat = ObserveDispatcherAsync(dispatchTimes, stopHeartbeat.Token);
                            var allocated = GC.GetTotalAllocatedBytes(true);
                            var start = Stopwatch.GetTimestamp();
                            double elapsed;
                            try
                            {
                                await update(i + Warmups);
                                Frame(window, viewer);
                                elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            }
                            finally
                            {
                                stopHeartbeat.Cancel();
                                await heartbeat;
                            }
                            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                            var drift = Math.Abs(viewer.Scroller!.Offset.Y - offset.Y);
                            var selectionPreserved = viewer.Session.Selection == selection;
                            if (!selectionPreserved) throw new InvalidOperationException($"{operation} changed the selection in an unchanged prefix.");
                            if (drift > 1) throw new InvalidOperationException($"markdown-{paragraphs}/{operation} sample {i}: viewport moved from {offset.Y:F2} to {viewer.Scroller.Offset.Y:F2} DIP (drift {drift:F2}).");
                            if (!viewer.Document.Text.Contains(expectedMarker(i + Warmups), StringComparison.Ordinal))
                                throw new InvalidOperationException($"{operation} did not display its latest source revision.");
                            if (viewer.IsParsing || viewer.ParseError is not null)
                                throw new InvalidOperationException($"{operation} did not finish successfully.", viewer.ParseError);
                            if (i >= 0) samples.Add(new(elapsed, bytes, viewer.Markdown.Length, drift, selectionPreserved, dispatchTimes.ToList()));
                        }
                        var times = samples.Select(s => s.Milliseconds).Order().ToArray();
                        var dispatch = samples.SelectMany(s => s.UiDispatchMilliseconds).Order().ToArray();
                        if (dispatch.Length == 0) throw new InvalidOperationException("No UI dispatcher observations were collected.");
                        var p95 = Percentile95(times);
                        var dispatchP95 = Percentile95(dispatch);
                        results.Add(new(paragraphs, operation, UpdateBudgetMilliseconds,
                            (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2, p95, p95 <= UpdateBudgetMilliseconds,
                            DispatchBudgetMilliseconds, dispatchP95, dispatchP95 <= DispatchBudgetMilliseconds, dispatch.Length,
                            samples.Select(s => s.AllocatedBytes).Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1),
                            samples.Max(s => s.ScrollDrift), samples));
                        Console.WriteLine($"markdown-{paragraphs}/{operation}: source-to-frame p95 {p95:F2} ms / {UpdateBudgetMilliseconds:F0} ms; UI dispatch p95 {dispatchP95:F2} ms / {DispatchBudgetMilliseconds:F0} ms");
                    }

                    await Measure("small-edit", i => viewer.UpdateMarkdownAsync(source + $"\n\nRevision {i:D4}."), i => $"Revision {i:D4}.");
                    await viewer.UpdateMarkdownAsync(source);
                    Frame(window, viewer);
                    await Measure("streamed-append", async i =>
                    {
                        viewer.AppendMarkdown($"\n\nStream item {i:D4}: **ready** and `code`.");
                        await viewer.WaitForParsingAsync();
                    }, i => $"Stream item {i:D4}:");

                    // Deliberately supersede pending parses without awaiting each update.
                    // The final rendered revision must win.
                    for (var revision = 0; revision < 30; revision++)
                        viewer.Markdown = source + $"\n\nBurst revision {revision:D4}.";
                    await viewer.WaitForParsingAsync();
                    Frame(window, viewer);
                    var burstCorrect = viewer.Document.Text.EndsWith("Burst revision 0029.", StringComparison.Ordinal);
                    if (!burstCorrect || viewer.ParseError is not null || viewer.IsParsing)
                        throw new InvalidOperationException("Rapid source updates did not display the final revision.", viewer.ParseError);
                    if (viewer.Session.Selection != selection || Math.Abs(viewer.Scroller!.Offset.Y - offset.Y) > 1)
                        throw new InvalidOperationException("Rapid source updates changed the unchanged selection or viewport.");
                    bursts.Add(new { initialParagraphs = paragraphs, revisions = 30, finalRevisionDisplayed = burstCorrect,
                        selectionPreserved = viewer.Session.Selection == selection,
                        scrollDrift = Math.Abs(viewer.Scroller!.Offset.Y - offset.Y) });
                }
                finally { window.Close(); }
            }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        var report = new
        {
            capturedUtc = DateTimeOffset.UtcNow,
            revision = Environment.GetEnvironmentVariable("TEXTALONIA_REVISION") ?? "unrecorded",
            os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), logicalProcessors = Environment.ProcessorCount,
            avalonia = typeof(Application).Assembly.GetName().Version?.ToString(),
            backend = "Avalonia.Headless with Skia software frame capture; native compositor, IME and device behavior excluded",
            viewport = "800 x 500 DIP; Inter 16; Fluent Light; no toolbar; offset 320 DIP",
            methodology = "Release; 3 warmups and 40 consecutive samples per operation and workload. Source-to-frame includes source assignment, debounce, asynchronous parsing/application, dispatcher drain, layout and frame capture. Independent worker posts a UI dispatcher callback every 1 ms while each update runs; queue-to-callback timings exclude the worker delay. p95 uses nearest rank. Allocations are process-wide GC.GetTotalAllocatedBytes deltas including the heartbeat, without forced collection. Small edits replace one suffix paragraph; appends add one Markdown paragraph per chunk. Each update asserts unchanged prefix selection, scroll retention within 1 DIP, and latest revision text. Rapid bursts publish 30 revisions without waiting between assignments.",
            budgets = new { sourceToFrameP95Milliseconds = UpdateBudgetMilliseconds, uiDispatchP95Milliseconds = DispatchBudgetMilliseconds,
                maximumScrollDriftDip = 1, scope = "100 and 1000 initial body paragraphs plus a heading; latency is reported rather than used as a machine-independent test failure" },
            results, bursts
        };
        File.WriteAllText(Path.Combine(destination, "markdown.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static Task ObserveDispatcherAsync(ConcurrentQueue<double> observations, CancellationToken stop) => Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            var start = Stopwatch.GetTimestamp();
            await Dispatcher.UIThread.InvokeAsync(() => observations.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
            try { await Task.Delay(1, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
    });

    private static double Percentile95(double[] ordered) => ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];

    private static string CreateSource(int paragraphs)
    {
        var source = new StringBuilder("# Markdown integration workload\n\n");
        for (var i = 0; i < paragraphs; i++)
            source.Append($"Paragraph {i:D4} has **strong text**, *emphasis*, a [link](https://example.com/), and `inline code`.\n\n");
        return source.ToString();
    }

    private static void Frame(Window window, MarkdownViewer viewer)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing Markdown preview frame.");
        if (viewer.LayoutError is { } error) throw new InvalidOperationException("Markdown layout failed.", error);
    }
}
