# Phase 7: XAML and Markdown integrations

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** additional document integrations that reuse the model, viewer, diagnostics, and resource services. **Entry:** Phases 3-4 and P5.1; can proceed alongside Phase 6. **Status:** planned.

## Starting points

- [Format registration/API](../src/Textalonia/Serialization/DocumentFormats.cs) and diagnostics from Phase 5.
- [TextaloniaViewer](../src/Textalonia/Controls/TextaloniaEditor.cs), [theme](../src/Textalonia/Themes/Generic.axaml), and Phase 4 inline descriptors.
- [Serialization tests](../tests/Textalonia.Tests/SerializationTests.cs) and [package consumer](../tests/Textalonia.PackageSmoke/Program.cs).

## Tasks

- [ ] **P7.1 - Define and implement a data-only XAML codec.** Specify a versioned Textalonia document vocabulary, supported nodes/properties, resource references, namespace, and extension selection. Parse/write whitelisted document data with XML size/depth limits; do not invoke a general XAML loader, markup extensions, event handlers, or arbitrary CLR constructors. Map registered inline descriptors as data. **Done when:** supported documents round-trip, unknown content is diagnosed/rejected according to options, and tests demonstrate that imported data cannot instantiate live controls. Do not claim compatibility with another editor's XAML vocabulary without fixtures.

- [ ] **P7.2 - Add a Markdown codec with an explicit dialect.** Record the parser/dependency decision and dialect, starting with headings, paragraphs, emphasis, links, lists, quotes, fenced code, and supported images; explicitly decide table/task-list extensions. Map to document structures, preserve code language metadata, and diagnose formatting without a Markdown representation. Define raw-HTML and resource/link policies consistently with Phase 5. **Done when:** dialect fixtures pass import/export/re-import for supported semantics, including nested lists, escaping, code fences, and unsupported-content reports.

- [ ] **P7.3 - Add a dedicated Markdown viewer.** Expose Markdown source and parse/error state through a viewer that reuses document rendering, themes, link activation, selection, and accessibility. Parse asynchronously with cancellation/revision checks so stale results cannot overwrite newer source; define append/update behavior and benchmark repeated updates. **Done when:** small edits and streamed appends preserve responsiveness and scroll behavior under the adopted budgets, and the host can control links and resources without replacing the viewer.

- [ ] **P7.4 - Add optional code highlighting.** Define a language/token-to-style adapter and cache/recompute policy for code blocks. Select any integration dependency only after reviewing its supported languages, maintenance, and redistribution terms. Keep highlighting presentation separate from original code text and serialized content. **Done when:** code renders without the optional adapter, enabling it does not change copied/exported text, and malformed/unknown languages or rapid source changes fail gracefully.

- [ ] **P7.5 - Demonstrate and document integration boundaries.** Add demo examples for XAML import/export, Markdown display, code highlighting, diagnostics, and custom resource handling. Decide whether optional integrations remain in the main package or companion packages; preserve the core package's ability to work without optional dependencies. Extend the independent consumer accordingly. **Done when:** a clean consumer can use each advertised integration and documentation lists dialect/vocabulary limits and dependency requirements.

## Exit gate

- Integration codecs share native model/resource semantics and diagnostics; none requires a live visual tree to parse or serialize.
- Markdown source changes cannot display stale results, and viewer selection/accessibility/native-resource behavior remains covered.
- Optional highlighting adds no mandatory highlighter dependency or changes to document text.
- XAML is documented as Textalonia's data vocabulary; native JSON and its existing extensions remain supported.

## Handoff

XAML and Markdown can be separate delivery tracks after the common contracts exist; highlighting follows the Markdown codec/viewer. Give Phase 8 public API snapshots, sample consumers, and dependency metadata for all integrations that will ship.
