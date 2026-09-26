# Phase 2 qualification capture

The final report links this archive. Standard seven-sample gates pass; the longer
strict-mode confirmation has two latency misses, so the Phase 2 exit gate remains open. Qualification results are complete process runs;
`status.json` is the completion marker and `budgets.json` uses the unchanged
thresholds in `docs/PERFORMANCE.md`. Do not substitute samples across runs.

## Reproduce the paired Phase 1 reference

From a clean, separate checkout of commit `ccae784`:

```sh
git apply /path/to/this/archive/p1-harness.patch
dotnet restore Textalonia.sln --configfile NuGet.Config
dotnet build Textalonia.sln -c Release --no-restore
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --warmups 2 --repetitions 7 --isolate-samples true --output artifacts/reference
```

The patch adds only the setup-isolation option and GC observations. It does not
change the old engine, corpus, UI operations, timed action or budget thresholds.
The corpus source is identical after normalizing line endings.

## Reproduce Phase 2

```sh
dotnet build Textalonia.sln -c Release --no-restore
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --warmups 2 --repetitions 7 --isolate-samples true --output artifacts/phase2/compatibility
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --warmups 2 --repetitions 7 --isolate-samples true --text-mode document --max-shaping-characters 4096 --output artifacts/phase2/document
pwsh -File scripts/Compare-BaselineBudgets.ps1 -Results artifacts/phase2/compatibility -Enforce
pwsh -File scripts/Compare-BaselineBudgets.ps1 -Results artifacts/phase2/document -Enforce
```

Repeat the document-mode command with `--repetitions 30` for the full-corpus confirmation capture. A rendering-limit
error in any benchmark frame fails the runner; the strict capture cannot pass
by timing the error message instead of the corpus. Run processes
sequentially on the reference machine. `host-notes.txt` records the actual setup;
`source-hashes.json` and `verification.json` identify tested/packed binaries.
The original and nonisolated results remain part of the evidence, not discarded
outliers. The controlled results qualify warm actions in the full-corpus order; they do not
assert a universal native input latency or a native-memory bound.

The full-corpus order and two warmups are part of the paired qualification method.
`diagnostics/first-candidate` preserves the previous binary: its standard captures
and 30-sample compatibility run passed, while its 30-sample document run was stopped
after 77 complete cases to investigate a 16.6832 ms end-deletion p95. Its status is
explicitly `interrupted`; it is not a complete qualification. The failing caret
visibility regression is retained there too. No completed samples were discarded.

`diagnostics/standalone-long-paragraph` measures the final binary with the long
paragraph as the only workload. Each mode misses its first insertion budget;
later end edits pass and shape one window after the scroll fix. These standalone
startup results remain disclosed; they do not replace the fixed full-corpus runs.