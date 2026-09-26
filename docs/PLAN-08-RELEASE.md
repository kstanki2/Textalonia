# Phase 8: API stabilization and release

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** a qualified public release with explicit compatibility and support commitments. **Entry:** Phases 1-7 exit evidence for the advertised release scope. **Status:** planned.

## Starting points

- [Public package configuration](../src/Textalonia/Textalonia.csproj), [shared build properties](../Directory.Build.props), and [CI](../.github/workflows/ci.yml).
- [Package consumer](../tests/Textalonia.PackageSmoke/Textalonia.PackageSmoke.csproj), [README](../README.md), [architecture](ARCHITECTURE.md), and this roadmap.
- Compatibility, qualification, performance, and corpus reports produced by the earlier phases.

## Tasks

- [ ] **P8.1 - Stabilize the public API and data contracts.** Review model, session, commands, input components, resource services, diagnostics, viewer, and codec interfaces against the baseline API. Capture an API compatibility baseline and document supported threading, binding, selection, exceptions, and disposal behavior. Add migration examples for deliberate preview breaks and a native schema support policy. **Done when:** public changes are intentional, consumer examples compile, and older native files load through tested migrations.

- [ ] **P8.2 - Complete platform and workload qualification.** Rerun the declared native IME, clipboard, keyboard/pointer/touch, screen-reader, theme/DPI, and mobile cases against the release candidate. Re-run performance and extended fuzz/corpus workloads on recorded environments. Tie evidence to a specific commit/package version. **Done when:** no unresolved failure contradicts an advertised support claim; missing/deferred platforms or features stay explicitly unqualified in the roadmap rather than disappearing from it.

- [ ] **P8.3 - Finalize release identity and metadata.** Have maintainers select ownership, project license, repository/project URLs, copyright, package IDs, and the version/release policy. Verify package ID availability/control and dependency notices, including any optional integrations. Add license/readme/source/symbol metadata to each shipped package. **Done when:** metadata contains real project values, ownership decisions are recorded, and package contents and dependency terms have been reviewed.

- [ ] **P8.4 - Validate clean package consumers.** Build/test/pack in the supported OS matrix, restore from the freshly built feed, and execute independent consumers for editor, viewer, custom format, input replacement, resources, and optional integrations. Verify source/symbol information, included docs, minimum/pinned supported dependencies, and packages' lack of an unintended desktop-host dependency. **Done when:** packed artifacts work independently of project references or stale local caches, and CI retains logs/packages for the candidate version.

- [ ] **P8.5 - Prepare the release candidate and publication procedure.** Write release notes, compatibility/support tables, adoption examples, known limitations, upgrade steps, and a repeatable publish/verification procedure. Distinguish preview distribution from a stable or parity claim. Include artifact/version identity and post-publish install checks; keep automatic publishing separate from ordinary validation. **Done when:** maintainers can review the exact candidate artifacts and release evidence, and a later explicit release action can publish those artifacts without rebuilding an unreviewed variant.

- [ ] **P8.6 - Publish and verify the selected release.** During the release action, publish the reviewed package/symbol artifacts and versioned release notes through the chosen process. Verify installation from the public feed in a clean consumer with the local feed disabled, confirm source/symbol resolution, and run the advertised examples. Record the released version and support status in the README/roadmap, with a documented correction or unlisting procedure for a broken package. **Done when:** public package availability and independent installation are verified and the release record points to the exact source and artifacts.

## Exit gate

- The candidate is reproducible, installable, documented, and qualified for every claim in its release notes.
- Native schema and public API compatibility rules are published with migration guidance.
- Ownership/license/package identity are resolved; platform and performance evidence is attached to the candidate.
- Deferred features remain unchecked and visible. A reduced-scope preview can ship without completing parity, but cannot close the omitted roadmap tasks.
- P8.1-P8.5 establish release-candidate readiness. Phase 8 is complete only after P8.6 verifies public publication and installation.

## Handoff

Metadata decisions may be prepared in Phase 1 and completed before feature work ends. Freeze APIs only after the model, input, resource, and codec extension points have been exercised by real consumers. Public publication is a separate release action; completing this planning work does not publish a package.
