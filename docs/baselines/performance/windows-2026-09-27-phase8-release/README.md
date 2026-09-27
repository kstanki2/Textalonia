# Windows Phase 8 candidate evidence

Exact working-tree sources and SDK/runtime are recorded alongside the measurements.
The candidate is 0.1.0-preview.1, base commit 4eead6b1e60b79c4e03663b0f6c90fbba8f0ef93, with
uncommitted release changes. This is local evidence; the clean committed three-OS
release gate, native/device qualification and public publication remain open.

Release build and all 513 tests passed with 2,000 fuzz operations per
seed. The copied, isolated package consumer passed. Portable symbols match the
package, and all 59 mapped C# source checksums were
retrieved and verified against public GitHub bytes. The single Avalonia-generated
XAML source exception is explicitly packaged and documented.

General workloads use 3 warmups and 15 repetitions. Compatibility mode recorded
43 budget misses across 138 cases; document mode recorded
18 across 90 cases. These include separate latency/allocation
checks per case; inspect budgets.json for each metric. PERF-01 remains open.
Interaction and Markdown probes retain their own samples and published budgets.

The candidate manifest hash is 2295a1219fde86afc5ccec7f39932089c258de8d4c63d4b83da5e29d426b8a20.
Local packages/logs/TRX are in artifacts/phase8-release. candidate.json preserves
their exact hashes; those binary/log artifacts are retained locally and by the CI
route, not embedded into this evidence directory. The source inventory describes
the validation snapshot before this receipt/evidence copy was added.

The first Source Link check found CRLF checkout bytes differing from GitHub's LF
blobs. The .gitattributes fix and LF-normalized build were revalidated. Earlier
scratch candidates remain in ignored artifacts directories; only this candidate
is used by the final receipt.
