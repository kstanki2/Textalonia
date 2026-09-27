using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Media;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.Model;

namespace Textalonia.Benchmarks;

// Reproducible exact-pagination measurements. This is an evidence capture, not a latency gate.
internal static class PaginationProbe
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        using var ui = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        ui.Dispatch(() =>
        {
            var samples = new List<object>();
            foreach (var count in new[] { 100, 1000 })
            {
                var document = new FlowDocument(Enumerable.Range(0, count).Select(i => new Paragraph(
                    $"Paragraph {i}: Exact pagination keeps text, caret positions and page geometry together. " +
                    "Fixed Inter typography provides a repeatable baseline for edits at either end of a report.")));
                using var engine = new PaginationEngine();
                var font = new FontFamily("Inter");
                for (var repetition = -1; repetition < 5; repetition++)
                {
                    var session = new EditorSession(document);
                    engine.Clear();
                    Capture("full", session.Document, repetition);
                    session.Select(0, 0); session.InsertText("An additional opening sentence changes the first paragraph. ");
                    Capture("edit-start", session.Document, repetition);
                    session.Select(session.Index.Length, session.Index.Length); session.InsertText(" Closing sentence.");
                    Capture("edit-end", session.Document, repetition);
                }
                void Capture(string operation, FlowDocument snapshot, int repetition)
                {
                    var measuredBefore = engine.MeasuredParagraphs;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    using var pages = engine.Paginate(snapshot, font);
                    var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (repetition >= 0) samples.Add(new { paragraphs = count, operation, repetition, milliseconds,
                        allocatedBytes, pages = pages.Pages.Length, fragments = pages.Fragments.Length,
                        engine.CachedMeasurements, engine.CachedLayoutBytes, engine.PeakLayoutBytes,
                        measuredParagraphs = engine.MeasuredParagraphs - measuredBefore, engine.ReusedCheckpoints, engine.ReflowedBlocks,
                        retainedManagedBytes = GC.GetTotalMemory(false) });
                }
            }
            File.WriteAllText(Path.Combine(output, "pagination-probe.json"), JsonSerializer.Serialize(new
            {
                capturedUtc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription, font = "Inter 16 DIP", backend = "Avalonia.Headless + Skia on UI thread",
                note = "One warmup and five repetitions. Full clears engine caches before measurement; edits reuse preceding layout. Exact complete layout; view zoom/DPI do not enter pagination. Managed heap is process-wide and is not cache accounting. No latency guarantee.",
                samples
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("Pagination probe complete: full pagination and edits near the start/end of 100/1000 paragraph documents.");
    }
}
