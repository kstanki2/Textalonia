using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Baselines;
using Textalonia.Controls;
namespace CpuProbe;
public class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        _ = typeof(TextaloniaEditor).Assembly;
        Styles.Add(new StyleInclude(new Uri("avares://Textalonia.Benchmarks/")) { Source = new Uri("avares://Textalonia/Themes/Generic.axaml") });
    }
}
public static class Bootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ProbeApp>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    private static ulong Cycles() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles : throw new InvalidOperationException("Cannot read thread cycles.");
    public static void Main(string[] args)
    {
        var samples = new List<object>();
        using var session = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
        session.Dispatch(() =>
        {
            var document = BaselineDocuments.Workload("table-heavy");
            foreach (var operation in new[] { "delete-middle-document", "caret-document" })
                for (var i = -10; i < 64; i++)
                {
                    var editor = new TextaloniaEditor { Document = document, SynchronizeText = false, MaxShapingCharacters = 4096, FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter") };
                    var window = new Window { Width = 800, Height = 500, Content = editor };
                    try
                    {
                        window.Show(); window.UpdateLayout(); editor.FocusDocument();
                        void Frame() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame."); }
                        editor.Session.Select(editor.Session.Index.Length / 2, editor.Session.Index.Length / 2); Frame();
                        GC.Collect(2, GCCollectionMode.Forced, true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, true);
                        var layout = editor.GetVisualDescendants().OfType<DocumentSurface>().Single().Layout;
                        var shapes = layout.ShapedParagraphs;
                        var paused = GC.GetTotalPauseDuration(); var cycles = Cycles(); var start = Stopwatch.GetTimestamp();
                        window.KeyPress(operation.StartsWith("delete") ? Key.Back : Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
                        var inputMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var inputCycles = Cycles() - cycles;
                        cycles = Cycles(); start = Stopwatch.GetTimestamp(); Frame();
                        var frameMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var frameCycles = Cycles() - cycles;
                        if (i >= 0) samples.Add(new { operation, i, inputMs, frameMs, inputCycles, frameCycles, totalMs = inputMs + frameMs, totalCycles = inputCycles + frameCycles, gcPauseMs = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds, shapes = layout.ShapedParagraphs - shapes });
                    }
                    finally { window.Close(); }
                }
        }, CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { note = "Diagnostic only: 10 warmups, 64 samples, table workload alone, per-stage Windows dispatcher-thread cycle counts. Other threads and frequency changes are not measured; does not replace full-corpus qualification.", samples }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Dispatcher CPU probe complete: 128 samples.");
    }
}