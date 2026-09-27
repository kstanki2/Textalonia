# Phase 6 interaction probe - Windows, 2026-09-26

Run from the repository root:

```powershell
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-restore -p:UsedAvaloniaProducts= -- --interaction-probe docs/baselines/performance/windows-2026-09-26-phase6-interactions
```

`interactions.json` records the environment, methodology, per-frame raw samples, allocations and cleanup assertions. `interaction-preview.png` shows the expanded toolbar, mixed-script text, nested/merged cells, rectangular selection and a column-resize preview. The initial exploratory report is retained as `interactions-initial-mixed-clock.json`.

This uses Avalonia.Headless with Skia frame capture, the default toolbar and eager Text synchronization. It measures dispatcher processing, layout and frame generation. It does not measure a native compositor, platform input, software keyboards or device touch behavior.

Three warmups precede each operation. There are 40 caret/viewport/preview samples, 20 width-resize samples and 80 samples per autoscroll mode. The p95 uses nearest rank. Budgets retain the existing 16 ms caret/scroll and 100 ms resize targets; a table preview uses the resize target.

| Workload | Caret p95 | Viewport p95 | Width resize p95 | Single-clock autoscroll p95 |
|---|---:|---:|---:|---:|
| 10,000 paragraphs | 5.24 ms | 5.06 ms | 84.42 ms | 4.60 ms |
| Long paragraph | 6.08 ms | 6.02 ms | **103.09 ms** | 4.39 ms |
| 1,000 table cells | 11.79 ms | 7.26 ms | 89.01 ms | 12.88 ms |

Table resize preview p95 was 59.64 ms. A separate stress mode keeps the real dispatcher clock running while explicitly advancing it before each rendered frame; a slow frame can therefore include two timer ticks. That stress mode measured p95 25.74 ms (10,000 paragraphs), 8.30 ms (long paragraph), and 32.84 ms (table-heavy). The second mode disables the automatic timer and executes exactly one production `Advance(16 ms)` per frame. It distinguishes per-tick work from the deliberately amplified stress load; production timer behavior remains covered by gesture tests.

**PERF-06 remains open:** the long-paragraph width-resize p95 exceeded 100 ms in the final run, and the mixed-clock stress mode exceeded 16 ms for paragraphs/table workloads. The earlier exploratory run measured long-paragraph resize at 48.38 ms and table stress autoscroll at 24.08 ms. These results show host/run variability and stress-load sensitivity; the probe does not establish a universal latency guarantee or close every performance gate.

All runs asserted resident glyph limits of 256 layouts / 16 MiB. Repeated previews preserved the document/history, cancellation restored the same document, and one resize commit required one undo. Autoscroll preserved its anchor and retained-history size. All three hosts released their glyph caches and stopped their timers on release/detach. These checks bound known resources; they are not a general managed-heap leak proof.
