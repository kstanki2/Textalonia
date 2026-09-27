using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Baselines;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Benchmarks;

internal static class InteractionProbe
{
    private sealed record Sample(double Milliseconds, long AllocatedBytes, int CachedLayouts, long CachedLayoutBytes, long RetainedHistoryBytes);
    private sealed record Result(string Workload, string Operation, double BudgetMilliseconds, double MedianMilliseconds,
        double P95Milliseconds, long P95AllocatedBytes, bool WithinLatencyBudget, List<Sample> Samples);

    private static void CapturePreview(string destination)
    {
        var nested = Table.Create(3, 3);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                nested = nested.SetCell(row, column, nested.Rows[row][column] with
                { Blocks = [new Paragraph($"Row {row + 1}, column {column + 1}")], Padding = new(10, 12, 10, 12) });
        nested = nested.MergeCells(0, 0, 1, 2);
        nested = nested.SetCell(0, 0, nested.Rows[0][0] with { Blocks = [new Paragraph("Merged heading")], Background = "#E5EDF8" });
        var outer = Table.Create(1, 1);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with
        { Blocks = [new Paragraph("Nested table with a rectangular selection"), nested], Padding = new(16, 16, 16, 16) });
        var editor = new TextaloniaEditor
        {
            Document = new FlowDocument([new Paragraph("Phase 6 interaction preview"),
                new Paragraph("Mixed text: abc \u05d0\u05d1\u05d2 123 (\u0645\u0631\u062d\u0628\u0627)"), outer,
                new Paragraph("Column resize preview - changes commit as one undo step.")]),
            FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter")
        };
        var window = new Window { Width = 960, Height = 720, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            editor.SelectTableCells(nested.Id, 1, 1, 2, 2);
            editor.BeginTableResize(nested.Id, TableResizeAxis.Column, 1, 280);
            editor.PreviewTableResize(330);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var image = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing preview frame.");
            image.Save(Path.Combine(destination, "interaction-preview.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            editor.CancelTableResize();
        }
        finally { window.Close(); }
    }
    public static void Run(string destination)
    {
        Directory.CreateDirectory(destination);
        var results = new List<Result>();
        var cleanup = new List<object>();
        using var ui = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        ui.Dispatch(() => CapturePreview(destination), CancellationToken.None).GetAwaiter().GetResult();
        ui.Dispatch(() =>
        {
            foreach (var workload in new[] { "paragraphs-10000", "long-paragraph", "table-heavy" })
            {
                var pointer = new DefaultPointerComponent();
                var document = BaselineDocuments.Workload(workload);
                var editor = new TextaloniaEditor { Document = document, PointerComponent = pointer,
                    FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter") };
                var window = new Window { Width = 800, Height = 500, Content = editor };
                DocumentSurface? surface = null;
                SelectionAutoScroller? scroll = null;
                try
                {
                    window.Show(); window.UpdateLayout(); editor.FocusDocument();
                    surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
                    void Frame()
                    {
                        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing headless frame.");
                        if (editor.LayoutError is { } error) throw error;
                        if (surface.Layout.CachedLayouts > ShapedLayoutCache.LayoutLimit || surface.Layout.CachedLayoutBytes > ShapedLayoutCache.ByteLimit)
                            throw new InvalidOperationException("Shaped layout cache exceeded its resident bound.");
                    }
                    void Measure(string operation, int count, double budget, Action<int> action)
                    {
                        var samples = new List<Sample>();
                        for (var i = -3; i < count; i++)
                        {
                            var bytes = GC.GetTotalAllocatedBytes(true);
                            var start = Stopwatch.GetTimestamp();
                            action(i + 3); Frame();
                            var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (i >= 0) samples.Add(new(duration, GC.GetTotalAllocatedBytes(true) - bytes,
                                surface.Layout.CachedLayouts, surface.Layout.CachedLayoutBytes, editor.Session.RetainedHistoryBytes));
                        }
                        var times = samples.Select(s => s.Milliseconds).Order().ToArray();
                        var p95 = times[(int)Math.Ceiling(times.Length * .95) - 1];
                        results.Add(new(workload, operation, budget, times[times.Length / 2], p95,
                            samples.Select(s => s.AllocatedBytes).Order().ElementAt((int)Math.Ceiling(times.Length * .95) - 1), p95 <= budget, samples));
                        Console.WriteLine($"{workload}/{operation}: median {times[times.Length / 2]:F2} ms; p95 {p95:F2} ms; budget {budget:F0} ms");
                    }
                    Frame();
                    editor.Session.Select(Math.Min(120, editor.Session.Index.Length), Math.Min(120, editor.Session.Index.Length)); Frame();
                    Measure("caret", 40, 16, i => window.KeyPress(i % 2 == 0 ? Key.Right : Key.Left, RawInputModifiers.None, PhysicalKey.None, null));
                    Measure("viewport-transition", 40, 16, i => editor.Scroller!.Offset = new Vector(0, i % 5 * 350));
                    Measure("width-resize", 20, 100, i => window.Width = i % 2 == 0 ? 780 : 800);
                    editor.Scroller!.Offset = default; Frame();
                    if (workload == "table-heavy")
                    {
                        var table = (Table)document.Blocks[0];
                        if (!editor.BeginTableResize(table.Id, TableResizeAxis.Column, 0, 76)) throw new InvalidOperationException("Resize did not begin.");
                        Measure("table-resize-preview", 40, 100, i => editor.PreviewTableResize(76 + i % 12));
                        if (!ReferenceEquals(editor.Session.Document, document) || editor.Session.CanUndo) throw new InvalidOperationException("Resize preview mutated history.");
                        editor.CancelTableResize(); Frame();
                        if (!ReferenceEquals(editor.Session.Document, document) || editor.Session.CanUndo || editor.TablePreviewDocument is not null)
                            throw new InvalidOperationException("Resize cancellation was not exact.");
                        editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 32);
                        for (var i = 0; i < 40; i++) editor.PreviewTableResize(40 + i);
                        if (!editor.CommitTableResize()) throw new InvalidOperationException("Resize did not commit.");
                        editor.Undo(); Frame();
                        if (!ReferenceEquals(editor.Session.Document, document) || editor.Session.CanUndo) throw new InvalidOperationException("Resize was not one undo entry.");
                    }
                    editor.Session.Select(0, 0); Frame();
                    var viewport = surface.InteractionViewport;
                    var startPoint = surface.TranslatePoint(new Point(viewport.Left + 40, viewport.Top + 35), window)!.Value;
                    window.MouseDown(startPoint, MouseButton.Left);
                    window.MouseMove(new Point(120, 580), RawInputModifiers.LeftMouseButton);
                    scroll = pointer.AutoScroller ?? throw new InvalidOperationException("Missing autoscroll controller.");
                    if (!scroll.IsActive) throw new InvalidOperationException("Selection gesture did not start.");
                    var anchor = editor.Session.Selection.Anchor;
                    var history = editor.Session.RetainedHistoryBytes;
                    Measure("stationary-edge-autoscroll-mixed-clock-stress", 80, 16, _ => scroll.Advance(TimeSpan.FromMilliseconds(16)));
                    if (editor.Session.Selection.Anchor != anchor || editor.Session.RetainedHistoryBytes != history)
                        throw new InvalidOperationException("Autoscroll changed its anchor or history.");
                    window.MouseUp(new Point(120, 580), MouseButton.Left); Frame();
                    if (scroll.IsTimerRunning || scroll.IsActive || surface.IsSelectingWithPointer) throw new InvalidOperationException("Autoscroll did not stop on release.");
                    // Measure one scheduled tick per frame separately from the stress
                    // driver above, whose real clock can tick during dispatcher drains.
                    editor.Session.Select(0, 0); editor.Scroller!.Offset = default; Frame();
                    viewport = surface.InteractionViewport;
                    scroll = new SelectionAutoScroller(new DocumentInputContext(surface, editor), () => true);
                    scroll.Begin(0, new Point(viewport.Left + 110, viewport.Bottom + 80), startTimer: false);
                    Measure("stationary-edge-autoscroll-manual-clock", 80, 16, _ => scroll.Advance(TimeSpan.FromMilliseconds(16)));
                    scroll.Stop();
                    if (editor.Session.Selection.Anchor != 0 || editor.Session.RetainedHistoryBytes != history || scroll.IsTimerRunning)
                        throw new InvalidOperationException("Manual autoscroll changed its anchor/history or retained a clock.");
                }
                finally
                {
                    window.Close();
                    if (surface is not null)
                    {
                        var released = surface.Layout.CachedLayouts == 0 && surface.Layout.CachedLayoutBytes == 0 && !(scroll?.IsTimerRunning ?? false);
                        cleanup.Add(new { workload, glyphCacheReleased = surface.Layout.CachedLayouts == 0, timerStopped = !(scroll?.IsTimerRunning ?? false), released });
                        if (!released) throw new InvalidOperationException("Detach retained a layout or active timer.");
                    }
                }
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
        var report = new
        {
            capturedUtc = DateTimeOffset.UtcNow,
            os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), logicalProcessors = Environment.ProcessorCount,
            backend = "Avalonia.Headless with Skia software frame capture; native compositor, IME and device behavior excluded",
            viewport = "800 x 500 DIP; Inter 16; Fluent Light; default toolbar; eager Text synchronization",
            methodology = "3 warmups per operation; 20 resize, 40 caret/viewport/preview or 80 autoscroll consecutive samples; all include dispatcher, layout and frame capture. p95 nearest rank. Allocations are process-wide GC.GetTotalAllocatedBytes deltas. No forced GC between samples. Mixed-clock stress combines explicit Advance with the live dispatcher timer; manual-clock samples use one Advance per frame with the automatic timer disabled. Both use identical production scrolling and selection logic.",
            budgets = "Existing 16 ms caret/scroll and 100 ms resize p95 targets; preview uses resize budget. Resident glyph bounds are 256 layouts / 16 MiB. Timer/cache cleanup and history invariants are asserted; this is not a general managed-heap leak proof.",
            results, cleanup
        };
        File.WriteAllText(Path.Combine(destination, "interactions.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
