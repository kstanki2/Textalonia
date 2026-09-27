# Markdown viewer update baseline

Captured on 2026-09-27 at 12:07 UTC from revision `81927070361f4d3ab8356ab4e5dd0fe75d0282d3` plus the Phase 7 working tree. The Release benchmark build completed with zero warnings and errors. The full environment, samples, allocation measurements, and correctness checks are in [markdown.json](markdown.json). The [source and assembly receipt](sources-sha256.json) records SHA256 hashes and confirms that the source set was identical before the build and after the probe.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:TEXTALONIA_REVISION = (git rev-parse HEAD).Trim() + '+phase7-working-tree'
dotnet build benchmarks\Textalonia.Benchmarks -c Release --no-restore
dotnet run --project benchmarks\Textalonia.Benchmarks -c Release --no-build -- --markdown-probe artifacts\benchmarks\phase7-markdown
```

The recorded host ran Windows build 26100, .NET 8.0.31 x64, SDK 10.0.204, and Avalonia 12.1.3, with 24 logical processors (`AMD64 Family 25 Model 33 Stepping 2, AuthenticAMD`). Rendering used Avalonia.Headless with Skia software frame capture, Fluent Light, Inter 16, and an 800 by 500 DIP viewport. No optional highlighting adapter or external resource load was enabled. The integrated test suite ran in a separate process during capture, so host scheduling contention is included.

The adopted budgets are 100 ms p95 from source update to rendered frame, 16 ms p95 for a queued UI callback, and at most 1 DIP of stationary scroll drift. Each case has three warmups followed by 40 measured updates. Source-to-frame timing includes source assignment, the viewer's debounce, asynchronous parsing and application, dispatcher draining, layout, and software frame capture. Percentiles use nearest rank.

| Initial body paragraphs | Update | Source-to-frame median | Source-to-frame p95 | UI callback p95 | Maximum UI callback |
| ---: | --- | ---: | ---: | ---: | ---: |
| 100 | Small suffix edit | 30.74 ms | 33.02 ms | 0.0917 ms | 2.5250 ms |
| 100 | Streamed paragraph append | 31.12 ms | 41.45 ms | 0.2037 ms | 5.3045 ms |
| 1000 | Small suffix edit | 33.83 ms | 51.62 ms | 1.9834 ms | 11.1846 ms |
| 1000 | Streamed paragraph append | 31.64 ms | 38.87 ms | 0.1071 ms | 1.3762 ms |

All cases meet both latency budgets. Each document also has a heading; body paragraphs combine bold, emphasis, links, and inline code. Edits replace one trailing revision paragraph, while appends add one Markdown paragraph per chunk. The selected unchanged prefix remained selected and the viewport remained exactly at 320 DIP throughout every update. Two additional bursts of 30 source assignments displayed the final revision and preserved selection and scroll.

An independent worker requested a UI callback every 1 ms while each update ran, producing 119-136 observations per measured case. The operating system controls actual timer resolution and scheduling; callback delay measures dispatcher responsiveness, not native compositor frame cadence or physical input latency. The burst check covers coalesced updates; controlled stale-completion tests separately exercise asynchronous races. Allocations are process-wide deltas, include the observer overhead, and do not measure retained heap or native resources. These results describe this host and workload; budget flags are reported rather than treated as machine-independent test failures.

The probe initially exposed scroll drift when rebuilt snapshots discarded persistent document tree measurements. The final run includes the fix that reuses unchanged tree branches and their layout measurements.
