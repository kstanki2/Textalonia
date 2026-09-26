# Development and diagnostic captures

These results are retained to disclose misses and the reason for each follow-up.
They are not substituted into a qualification run. The final source hashes and
full-corpus qualification are in the parent archive.

| Folder | Context | Outcome |
| --- | --- | --- |
| `before` | Starting engine, original nonisolated harness | 40 latency misses |
| `diagnostic` | Strict shaping and first allocation changes, nonisolated | 20 latency misses |
| `allocation-pass` | Intermediate reference visitors, nonisolated | 21 latency misses |
| `first-candidate` | Earlier candidate before fixing distant-caret scroll ordering; its hashes are inside | Complete seven-sample captures passed; complete 30-sample compatibility passed; strict confirmation stopped after 77 cases with one latency miss |
| `standalone-long-paragraph` | Final binary, 30 samples, only the long-paragraph workload | First insertion misses the latency budget in each mode; end edits pass after the scroll fix |

The first candidate's `confirmation-30/document/status.json` deliberately records
`interrupted`. Its completed raw samples remain intact. `scroll-regression-before.trx`
records both caret-visibility failures before the fix; the final full test run has
107 passing tests, including those regressions.

The first three folders are intermediate development captures, not measurements of
the final binary. The final binary's nonisolated full-corpus capture is the sibling
`nonisolated-final` folder (23 latency misses). See the phase report for scope and
reproduction commands.