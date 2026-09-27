# Retained benchmark baselines

These are historical Windows development measurements from 2026-09-26 and
2026-09-27. They describe the recorded sources and host, not the current checkout
or a certified release. The selected CSV files retain every case in each listed
run. [Run metadata](baselines/curated/runs.json) records the original environment,
arguments, revision strings, budget counts and SHA-256 hashes of archived inputs.

## Full-control runs

The host used Windows build 26100 x64, .NET 8.0.31, SDK 10.0.204, Avalonia 12.1.3
and 24 logical processors. Rendering used Avalonia.Headless with Skia software
capture, Inter 16 DIP, Fluent Light and an 800 x 500 DIP viewport at scale 1.
This does not measure a native compositor, input hardware or native working set.

| Run | Cases | Samples per case | Setup GC isolation | Failed budget checks / total |
| --- | ---: | ---: | --- | ---: |
| [initial-compatibility](baselines/curated/initial-compatibility.csv) | 138 | 7 | false | 102/234 |
| [phase2-reference](baselines/curated/phase2-reference.csv) | 138 | 7 | true | 59/234 |
| [phase2-compatibility](baselines/curated/phase2-compatibility.csv) | 138 | 7 | true | 0/234 |
| [phase2-document](baselines/curated/phase2-document.csv) | 90 | 7 | true | 0/144 |
| [phase2-confirmation](baselines/curated/phase2-confirmation.csv) | 90 | 30 | true | 2/144 |
| [phase2-nonisolated](baselines/curated/phase2-nonisolated.csv) | 138 | 7 | false | 23/234 |
| [release-compatibility](baselines/curated/release-compatibility.csv) | 138 | 15 | false | 43/234 |
| [release-document](baselines/curated/release-document.csv) | 90 | 15 | false | 18/144 |

Budget checks include latency, allocations and retained history, so their count
differs from the number of cases. CSV allocation columns are medians; they cannot
reconstruct p95 allocation checks without the archived raw samples. The adopted
[budgets and methodology](PERFORMANCE.md) remain unchanged.

The paired Phase 2 reference adds setup isolation and GC observations to the
initial harness; it does not change the reference engine or timed actions.
Seven-sample compatibility and strict document runs passed their checks. The
30-sample document confirmation still missed the 16 ms target for table deletion
(19.84 ms p95) and caret movement (16.33 ms). The final nonisolated capture had
23 latency misses. Standalone long-paragraph startup probes also missed targets.
The implementation was accepted with these gaps tracked as
[PERF-01](PERFORMANCE.md#perf-01-residual-latency-qualification).

The release-development captures used three warmups and 15 samples without setup
isolation. Compatibility and document modes recorded 43 and 18 failed checks,
respectively. They were made from a dirty development tree and do not qualify a
clean publication candidate. Runs with different isolation, source revisions or
workload order must not be treated as interchangeable samples.

## Interaction and Markdown probes

| Probe | Cases | Source/frame latency misses | Dispatcher latency misses |
| --- | ---: | ---: | ---: |
| [interactions-2026-09-26](baselines/curated/interactions-2026-09-26.csv) | 16 | 3 | Not measured separately |
| [markdown-2026-09-27](baselines/curated/markdown-2026-09-27.csv) | 4 | 0 | 0 |
| [release-interactions](baselines/curated/release-interactions.csv) | 16 | 1 | Not measured separately |
| [release-markdown](baselines/curated/release-markdown.csv) | 4 | 0 | 0 |

The 2026-09-26 interaction probe measured long-paragraph resize at 103.09 ms p95
against 100 ms. Mixed-clock stress combines manual advancement with the live
timer and also exceeded the 16 ms target for paragraph/table workloads. The
single-clock mode measures one production tick per frame. These are different
loads; [PERF-06](PERFORMANCE.md#perf-06-interaction-latency-qualification) stays open.
Cache/timer cleanup and history invariants passed; this is not a general leak proof.

The original Markdown probe used three warmups and 40 suffix edits or streamed
appends on 100/1,000 body paragraphs. All four cases met the 100 ms source-to-frame
and 16 ms dispatcher p95 budgets with zero observed scroll drift. A separate test
process ran during that capture. The release probe is retained separately; inspect
its CSV rather than transferring the earlier pass to a later source revision.

## Raw archive and new captures

Full samples, intermediate/failed captures, diagnostic source, old phase reports
and receipts are preserved in the maintainer's `legacy-docs.zip` archive. Its hash
and the local cleanup archive path are recorded in `runs.json`. The archive is
outside Git and is not downloaded with a clone. The local history backup also
preserves the original commits. Keep these recovery files separately when sharing
the historical evidence; the compact summaries are not a replacement for raw data
when re-evaluating a measurement or its provenance.

Write new reports under ignored `artifacts/benchmarks/` or a release-candidate
output directory. CI uploads raw reports as artifacts; retain required evidence
before its expiry, or attach it to the reviewed release. Commit only curated
summaries and environment metadata needed for ongoing comparisons. Raw data does
not belong under `docs/baselines/performance/`.

Use the commands in [performance](PERFORMANCE.md) and [release](RELEASE.md) to
capture fresh results. Historical receipts and pre-cleanup commit IDs are not
publication approval; rebuild and revalidate candidates from the intended clean,
public commit after a history rewrite.
