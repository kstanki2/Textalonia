These complete nonisolated diagnostics informed the implementation; they are not substituted for individual samples in the final captures.
before: unmodified 6ac4f30, original two warmups/seven samples; binary copy preserved locally in artifacts/phase2-final/before-binary.
diagnostic: opt-in shaping limit and first allocation changes, plus GC-pause observations; before the retention visitor change.
allocation-pass: retention visitor and geometry lookup changes; before the final paragraph-length and no-op preedit optimizations.
The two intermediate captures are identified by implementation stage, not advertised as measurements of the final binary. Only the final captures have matching final source/assembly hashes.
All three still have p95 latency misses; no slow sample has been deleted, masked or subtracted.
