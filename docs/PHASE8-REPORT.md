# Phase 8 implementation report

Phase 8 now supplies a reviewable release-candidate route. Public publication and
native certification are still open; this implementation does not claim Phase 8
complete. The package is MIT licensed following the maintainer's 2026-09-27 choice.

| Task | Delivered / remaining |
| --- | --- |
| P8.1 | Existing API signature baseline preserved; nullable/attribute/accessor metadata baseline added; SDK package API validation enabled; threading, binding, selection, ownership, failure and schema support contracts published. Frozen older-file migration and codec I/O-failure coverage plus compiled consumer extension examples added. |
| P8.2 | Candidate-specific environment/source/artifact receipts, extended fuzz/corpus and both performance modes plus interaction/Markdown probes are automated. Native IME, clipboard, input/touch, screen readers, DPI/themes and mobile devices remain unqualified. PERF-01 and application-export corpus gaps remain visible. |
| P8.3 | MIT license, contributor attribution, project/repository URLs, NuGet readme/docs/notices, source and portable symbols metadata are present. Resolved dependency terms are inventoried. Authenticated package control and final copyright/owner confirmation remain maintainer gates. |
| P8.4 | Isolated copied consumer, exact local feed mapping, empty cache, pinned/minimum dependency checks, no desktop-host graph, package contents and symbol/source inspection implemented. Three-OS CI retains candidate bundles/logs. Local execution does not substitute for completed remote matrix jobs. |
| P8.5 | Preview release notes, support/compatibility tables, adoption/migration examples and repeatable hash-checked publication/verification/correction procedure implemented. Candidate artifacts can be published unchanged after explicit review. |
| P8.6 | Explicit publish and clean public verification scripts are available. No package, symbols, tag or versioned release notes have been published by this implementation. |

## Verification

The dated [verification receipt](baselines/phase8-verification.json) records actual
local results and candidate paths/hashes. The candidate bundle retains the full
TRX, logs, package and symbol inspection, source/dependency inventories, consumer
receipt, pending native records and measured performance reports.

The release verifier rejects changed files/manifests and dirty or incompletely
qualified publication candidates. The public-feed path is deliberately distinct
from local verification and requires public symbols/source to match reviewed
bytes. No public-feed success is claimed before a package is published.

## Remaining release decisions

Confirm the authenticated NuGet owner and package-ID control; confirm contributor
copyright/repository authority; select an immutable public preview version; run
the three-OS extended workflow at the committed source; review performance misses
and the explicitly reduced native support scope. Native/device/application
qualification continues under its original task IDs and is not closed by a
reduced preview. Follow [RELEASE.md](RELEASE.md) for the later release action.
