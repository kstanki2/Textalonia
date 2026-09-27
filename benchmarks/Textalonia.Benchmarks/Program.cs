using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Baselines;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Benchmarks;

public sealed class BenchmarkApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Textalonia/")) { Source = new Uri("avares://Textalonia/Themes/Generic.axaml") });
    }
}

public static class Bootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BenchmarkApp>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class Program
{
    private static readonly string[] Workloads = ["paragraphs-100", "paragraphs-1000", "paragraphs-10000", "long-paragraph", "table-heavy", "run-heavy"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Counters(int ShapedParagraphs, int CachedParagraphs, int UpdatedIndexNodes, long RetainedHistoryBytes,
        long ShapedCharacters, int LargestShapingWindow, int CachedLayouts, long CachedLayoutBytes, long PeakLayoutBytes, int GeometryNodes);
    private sealed record Collections(int Gen0, int Gen1, int Gen2);
    private sealed record Sample(double Milliseconds, long AllocatedBytes, long? RetainedUndoBytes, Counters? Counters, Collections Collections, double GcPauseMilliseconds);
    private sealed record Result(string Workload, string Operation, int Paragraphs, int Utf16Length, int NativeBytes,
        double MedianMs, double P95Ms, long MedianAllocatedBytes, long? MedianRetainedUndoBytes, double? BudgetMs, bool? WithinLatencyBudget, List<Sample> Samples);
    private sealed record Operation(Func<long?> Run, Action Cleanup, Func<Counters?>? Observe = null);
    private static bool _synchronizeText = true;
    private static int _maxShapingCharacters;

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            if (args is ["--interaction-probe", var interactionDestination]) { InteractionProbe.Run(interactionDestination); return 0; }
            if (args is ["--core-probe", var probeDestination]) { CoreProbe.Run(probeDestination); return 0; }
            if (args is ["--capture-contracts", var destination])
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "native-current.json"), DocumentFormats.Json.Serialize(BaselineDocuments.Structured()) + "\n");
                File.WriteAllText(Path.Combine(destination, "public-api.txt"), PublicApi.Capture());
                return 0;
            }
            if (args is ["--export-fixtures", var fixtures])
            {
                Directory.CreateDirectory(fixtures);
                foreach (var name in BaselineDocuments.Names)
                    File.WriteAllText(Path.Combine(fixtures, name + ".textalonia"), DocumentFormats.Json.Serialize(BaselineDocuments.Create(name)));
                File.WriteAllText(Path.Combine(fixtures, "scroll.textalonia"), DocumentFormats.Json.Serialize(BaselineDocuments.Workload("paragraphs-100")));
                return 0;
            }
            var options = Parse(args);
            var textMode = options.GetValueOrDefault("--text-mode", "compatibility");
            if (textMode is not ("compatibility" or "document")) throw new ArgumentException("Text mode must be compatibility or document.");
            _synchronizeText = textMode == "compatibility";
            _maxShapingCharacters = int.Parse(options.GetValueOrDefault("--max-shaping-characters", "0"), CultureInfo.InvariantCulture);
            if (_maxShapingCharacters != 0 && _maxShapingCharacters < 2048) throw new ArgumentException("Shaping limit must be zero or at least 2048.");
            var output = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/benchmarks/latest"));
            Directory.CreateDirectory(output);
            var warmups = int.Parse(options.GetValueOrDefault("--warmups", "2"), CultureInfo.InvariantCulture);
            var repetitions = int.Parse(options.GetValueOrDefault("--repetitions", "7"), CultureInfo.InvariantCulture);
            if (warmups is < 1 or > 100 || repetitions is < 3 or > 200) throw new ArgumentException("Use 1-100 warmups and 3-200 repetitions.");
            var isolateSamples = bool.Parse(options.GetValueOrDefault("--isolate-samples", "false"));
            var chosen = options.TryGetValue("--workload", out var single) ? new[] { single } : Workloads;
            if (chosen.Any(w => !Workloads.Contains(w))) throw new ArgumentException("Unknown workload.");
            var environment = new
            {
                startedUtc = DateTimeOffset.UtcNow, revision = Environment.GetEnvironmentVariable("TEXTALONIA_REVISION") ?? "unrecorded",
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription, sdk = Environment.GetEnvironmentVariable("TEXTALONIA_SDK") ?? "record with dotnet --info",
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "record host CPU separately",
                logicalProcessors = Environment.ProcessorCount, availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                avalonia = typeof(Application).Assembly.GetName().Version?.ToString(),
                backend = "Avalonia.Headless + Skia software drawing (not native compositor latency)",
                font = "Inter, 16 DIP; OS fallback for missing glyphs", theme = "Fluent Light", viewport = "800 x 500 DIP", dpiScale = 1,
                configuration = "Release required", textMode, maxShapingCharacters = _maxShapingCharacters, isolateSamples, warmups, repetitions, workloads = chosen,
                percentile = "nearest rank ceil(0.95 * n); median averages central pair", historyEntries = 100,
                allocationMethod = "GC.GetTotalAllocatedBytes(true), process-wide delta; setup and cleanup excluded",
                historyMethod = "forced-GC live heap with 100 entries minus same session after UndoLimit=0; signed noisy estimate",
                arguments = args
            };
            File.WriteAllText(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(environment, JsonOptions));
            File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"running\"}");
            var results = new List<Result>();
            using var ui = HeadlessUnitTestSession.StartNew(typeof(Bootstrap));
            foreach (var name in chosen)
            {
                var document = BaselineDocuments.Workload(name);
                document.Validate();
                var text = document.Text;
                var encoded = Encoding.UTF8.GetBytes(DocumentFormats.Json.Serialize(document));
                if (encoded.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Workload exceeds native import limit.");
                void Measure(string operationName, Func<Operation> prepare)
                {
                    var samples = new List<Sample>();
                    for (var i = -warmups; i < repetitions; i++)
                    {
                        var operation = prepare();
                        try
                        {
                            // Setup constructs a fresh control per sample. Settle its
                            // garbage before timing, so a previous setup's background
                            // collection cannot be charged to this action. Collections
                            // caused by the measured action remain inside the timer.
                            if (isolateSamples)
                            {
                                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                                GC.WaitForPendingFinalizers();
                                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                            }
                            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
                            var allocated = GC.GetTotalAllocatedBytes(true);
                            var paused = GC.GetTotalPauseDuration();
                            var start = Stopwatch.GetTimestamp();
                            var retained = operation.Run();
                            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            var gcPause = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds;
                            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                            if (i >= 0) samples.Add(new(elapsed, bytes, retained, operation.Observe?.Invoke(),
                                new(GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2), gcPause));
                        }
                        finally { operation.Cleanup(); }
                    }
                    var times = samples.Select(s => s.Milliseconds).Order().ToArray();
                    var median = (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2;
                    var p95 = times[(int)Math.Ceiling(times.Length * .95) - 1];
                    var budget = Budget(operationName);
                    var retainedValues = samples.Where(s => s.RetainedUndoBytes.HasValue).Select(s => s.RetainedUndoBytes!.Value).Order().ToArray();
                    var result = new Result(name, operationName, new DocumentIndex(document).Paragraphs.Length, text.Length, encoded.Length,
                        median, p95, samples.Select(s => s.AllocatedBytes).Order().ElementAt(samples.Count / 2),
                        retainedValues.Length == 0 ? null : retainedValues[retainedValues.Length / 2], budget, budget is null ? null : p95 <= budget, samples);
                    results.Add(result);
                    WriteResults(output, results);
                    Console.WriteLine($"{name}/{operationName}: median {median:F2} ms, p95 {p95:F2} ms");
                }

                ui.Dispatch(() =>
                {
                    Measure("first-viewport", () =>
                    {
                        EditorHost? host = null;
                        // Input-size reporting may have indexed the fixture. A fresh wrapper
                        // keeps first-open indexing inside the timer, as it was in P1.
                        return new(() => { host = new(document with { }, false); host.Frame(); return null; }, () => host?.Dispose(), () => host?.Observe());
                    });
                    foreach (var bound in _synchronizeText ? new[] { false, true } : new[] { false })
                    {
                        var mode = !_synchronizeText ? "document" : bound ? "bound" : "unbound";
                        foreach (var position in new[] { "start", "middle", "end" })
                        {
                            var offset = position == "start" ? 0 : position == "middle" ? text.Length / 2 : text.Length;
                            Measure($"type-{position}-{mode}", () => Prepare(document, bound, h => h.Editor.Session.Select(offset, offset),
                                h => h.Window.KeyTextInput("x")));
                            Measure($"delete-{position}-{mode}", () => Prepare(document, bound, h => h.Editor.Session.Select(offset, offset),
                                h => h.Window.KeyPress(position == "start" ? Key.Delete : Key.Back, RawInputModifiers.None, PhysicalKey.None, null)));
                        }
                        Measure($"caret-{mode}", () => Prepare(document, bound,
                            h => h.Editor.Session.Select(text.Length / 2, text.Length / 2),
                            h => h.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null)));
                        Measure($"text-update-{mode}", () => Prepare(document, bound, _ => { }, h =>
                        {
                            if (bound) h.Model.Text = text + "!";
                            else h.Editor.Text = text + "!";
                        }));
                    }
                    Measure("scroll", () => Prepare(document, false, _ => { }, h => h.Scroller.Offset = new Vector(0, h.Scroller.Extent.Height / 2)));
                    Measure("resize", () => Prepare(document, false, _ => { }, h => h.Window.Width = 640));
                    var needle = name.StartsWith("paragraphs-", StringComparison.Ordinal) ? "NEEDLE" : name == "table-heavy" ? "café" : name == "run-heavy" ? "run00" : "Long";
                    Measure("replace-all", () => Prepare(document, false, _ => { }, h =>
                    {
                        if (h.Editor.ReplaceAll(needle, "replaced") == 0) throw new InvalidOperationException("Replace workload has no matches.");
                    }));
                }, CancellationToken.None).GetAwaiter().GetResult();

                // Run asynchronous codecs outside the UI context, on caller-owned memory streams.
                Measure("native-save", () =>
                {
                    var stream = new MemoryStream();
                    return new(() => { DocumentFormats.Json.SaveAsync(document, stream).GetAwaiter().GetResult(); return null; }, stream.Dispose);
                });
                Measure("native-load", () =>
                {
                    var stream = new MemoryStream(encoded, false);
                    return new(() => { var loaded = DocumentFormats.Json.LoadAsync(stream).GetAwaiter().GetResult(); GC.KeepAlive(loaded); return null; }, stream.Dispose);
                });
                Measure("history-100", () =>
                {
                    var editor = new EditorSession(document) { UndoLimit = 100 };
                    return new(() =>
                    {
                        for (var i = 0; i < 100; i++)
                        {
                            var offset = i % 3 == 0 ? 0 : i % 3 == 1 ? editor.Index.Length / 2 : editor.Index.Length;
                            editor.Select(offset, offset); editor.InsertText("x");
                        }
                        var retained = GC.GetTotalMemory(true);
                        editor.UndoLimit = 0;
                        var released = GC.GetTotalMemory(true);
                        GC.KeepAlive(editor);
                        return retained - released;
                    }, () => { });
                });
            }
            File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "complete", completedUtc = DateTimeOffset.UtcNow, cases = results.Count }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--output" or "--warmups" or "--repetitions" or "--workload" or "--text-mode" or "--max-shaping-characters" or "--isolate-samples"))
                throw new ArgumentException("Options: --output PATH --warmups N --repetitions N --workload NAME --text-mode compatibility|document --max-shaping-characters N --isolate-samples true|false; or --export-fixtures PATH; or --capture-contracts PATH.");
            options.Add(args[i], args[i + 1]);
        }
        return options;
    }

    private static double? Budget(string operation) => operation switch
    {
        "first-viewport" => 250, "resize" or "text-update-bound" or "text-update-unbound" or "text-update-document" => 100,
        "replace-all" or "native-save" or "native-load" => 2000, "history-100" => null, _ => 16
    };

    private static void WriteResults(string output, List<Result> results)
    {
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, JsonOptions));
        var csv = new StringBuilder("workload,operation,paragraphs,utf16_length,native_bytes,median_ms,p95_ms,median_allocated_bytes,median_retained_undo_bytes,budget_ms,within_latency_budget\n");
        foreach (var r in results)
            csv.AppendLine($"{r.Workload},{r.Operation},{r.Paragraphs},{r.Utf16Length},{r.NativeBytes},{r.MedianMs:F4},{r.P95Ms:F4},{r.MedianAllocatedBytes},{r.MedianRetainedUndoBytes},{r.BudgetMs},{r.WithinLatencyBudget}");
        File.WriteAllText(Path.Combine(output, "summary.csv"), csv.ToString());
    }

    private static Operation Prepare(FlowDocument document, bool bound, Action<EditorHost> setup, Action<EditorHost> action)
    {
        var host = new EditorHost(document, bound);
        try { setup(host); host.Frame(); }
        catch { host.Dispose(); throw; }
        var before = host.Observe(); var revision = host.Editor.Session.Revision;
        return new(() => { action(host); host.Frame(); return null; }, host.Dispose, () =>
        {
            var after = host.Observe();
            return after with { ShapedParagraphs = after.ShapedParagraphs - before.ShapedParagraphs,
                ShapedCharacters = after.ShapedCharacters - before.ShapedCharacters,
                UpdatedIndexNodes = revision == host.Editor.Session.Revision ? 0 : after.UpdatedIndexNodes };
        });
    }

    private sealed class TextModel : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class EditorHost : IDisposable
    {
        public TextaloniaEditor Editor { get; } = new() { FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter") };
        public Window Window { get; }
        public TextModel Model { get; } = new();
        public ScrollViewer Scroller => Editor.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_ScrollViewer");
        public EditorHost(FlowDocument document, bool bound)
        {
            Editor.SynchronizeText = _synchronizeText;
            Editor.MaxShapingCharacters = _maxShapingCharacters;
            if (bound)
            {
                Editor.DataContext = Model;
                Editor.Bind(TextaloniaEditor.TextProperty, new Binding(nameof(TextModel.Text)) { Mode = BindingMode.TwoWay });
            }
            Editor.Document = document;
            Window = new Window { Width = 800, Height = 500, Content = Editor };
            Window.Show(); Window.UpdateLayout(); Editor.FocusDocument();
        }
        public Counters Observe()
        {
            var layout = Editor.GetVisualDescendants().OfType<DocumentSurface>().Single().Layout;
            return new(layout.ShapedParagraphs, layout.CachedParagraphs, Editor.Session.Index.Tree.UpdatedNodes, Editor.Session.RetainedHistoryBytes,
                layout.ShapedCharacters, layout.LargestShapingWindow, layout.CachedLayouts, layout.CachedLayoutBytes, layout.PeakLayoutBytes, layout.GeometryNodes);
        }
        public void Frame()
        {
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
            if (Editor.LayoutError is { } error) throw new InvalidOperationException("Benchmark content exceeded its shaping limit.", error);
        }
        public void Dispose() => Window.Close();
    }
}
