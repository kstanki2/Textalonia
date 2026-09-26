using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Textalonia.Baselines;
using Textalonia.Editing;

namespace Textalonia.Benchmarks;

// A small end-to-end session prototype, separate from the authoritative full UI
// benchmark. The copying comparator is intentionally identified as a model of
// the P1 bottleneck, not a re-execution of the historical P1 binary.
internal static class CoreProbe
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var samples = new List<object>();
        foreach (var workload in new[] { "paragraphs-100", "paragraphs-10000", "long-paragraph" })
            foreach (var mode in new[] { "copying-prototype", "persistent-session", "persistent-session-eager-text" })
                for (var repetition = -2; repetition < 7; repetition++)
                {
                    var document = BaselineDocuments.Workload(workload);
                    var session = new EditorSession(document);
                    var lines = session.Index.Paragraphs.Select(p => p.Paragraph.Text).ToArray();
                    var retained = new List<string[]>();
                    var allocated = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.GetTimestamp();
                    var maxNodes = 0;
                    for (var edit = 0; edit < 100; edit++)
                    {
                        if (mode == "copying-prototype")
                        {
                            var text = string.Join('\n', lines);
                            var offset = edit % 3 == 0 ? 0 : edit % 3 == 1 ? text.Length / 2 : text.Length;
                            var boundaries = StringInfo.ParseCombiningCharacters(text);
                            var boundary = Array.BinarySearch(boundaries, offset);
                            if (boundary < 0 && offset < text.Length) offset = boundaries[Math.Max(0, ~boundary - 1)];
                            var row = 0; var start = 0;
                            while (row + 1 < lines.Length && start + lines[row].Length < offset) start += lines[row++].Length + 1;
                            retained.Add(lines); lines = (string[])lines.Clone(); lines[row] = lines[row].Insert(offset - start, "x");
                            GC.KeepAlive(string.Join('\n', lines));
                        }
                        else
                        {
                            var offset = edit % 3 == 0 ? 0 : edit % 3 == 1 ? session.Index.Length / 2 : session.Index.Length;
                            session.Select(offset, offset); session.InsertText("x");
                            maxNodes = Math.Max(maxNodes, session.Index.Tree.UpdatedNodes);
                            if (mode == "persistent-session-eager-text") GC.KeepAlive(session.Index.Text);
                        }
                    }
                    var milliseconds = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (repetition >= 0) samples.Add(new { workload, mode, repetition, edits = 100, milliseconds, allocatedBytes = bytes,
                        maxUpdatedNodes = maxNodes, retainedHistoryEstimate = session.RetainedHistoryBytes });
                    GC.KeepAlive(retained); GC.KeepAlive(session);
                }
        File.WriteAllText(Path.Combine(output, "core-probe.json"), JsonSerializer.Serialize(new
        {
            note = "Copying comparator models flattening, whole-text grapheme scan, immutable paragraph array and string copying; use P1/P2 full control archives for release decisions.",
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Core probe complete: 63 measured 100-edit batches.");
    }
}
