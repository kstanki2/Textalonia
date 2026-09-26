# Table CPU diagnostic

This Windows-only probe uses the final packaged library, the unchanged table-heavy
fixture, 10 warmups and 64 samples per operation in a separate headless process.
It records dispatcher-thread cycles with QueryThreadCycleTime, separately around
input dispatch and frame completion. The stage totals exclude the cycle-reading
calls; other threads and clock-frequency changes are not measured. It is a
diagnostic, not a replacement for the authoritative full-corpus timing interval.

The longer-run misses did not reproduce: deletion p95 was 13.0641 ms (maximum
13.8752), and caret p95 was 8.4807 ms (maximum 9.013). No sample exceeded 16 ms.
This does not establish the cause of the full-corpus misses or close the gate.
All 128 samples are in results.json and the complete probe source is included.

Loaded library SHA-256:
d6787d1a25641a1940958c317f33d967a6889b662ecb1bcc7a5dadecf1189194

After packing the library, restore this project with a NuGet configuration that
includes that package and Avalonia 12.1.3, build Release, and run its executable
with an output JSON path as its only argument. The assembly name intentionally
matches the existing internal benchmark friend assembly. No product API was added
for this diagnostic.