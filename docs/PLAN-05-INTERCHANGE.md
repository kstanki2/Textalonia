# Phase 5: Conversion fidelity and structured clipboard

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** documented, testable content exchange with visible explanations for unsupported content. **Entry:** Phases 3-4 model/resource contracts; fixture collection can begin in Phase 1. **Status:** implementation and automated corpus complete; application corpus/native qualification open. See [implementation report and remaining gates](PHASE5-REPORT.md).

## Starting points

- [Format API/native storage](../src/Textalonia/Serialization/DocumentFormats.cs), [HTML](../src/Textalonia/Serialization/HtmlDocumentFormat.cs), [RTF](../src/Textalonia/Serialization/RtfDocumentFormat.cs), and [DOCX](../src/Textalonia/Serialization/DocxDocumentFormat.cs).
- [CopySelection/InsertDocument](../src/Textalonia/Editing/EditorSession.cs) and [platform clipboard adapters](../src/Textalonia/Controls/TextaloniaEditor.cs).
- [Serialization tests](../tests/Textalonia.Tests/SerializationTests.cs) and [demo file operations](../samples/Textalonia.Demo/MainWindow.axaml.cs).

## Tasks

- [x] **P5.1 - Add conversion diagnostics without breaking custom formats.** Design an additive result/options API around `IDocumentFormat` for diagnostic code, severity, source location/model ID where available, unsupported feature, and fallback taken. Support tolerant import and strict loss-detection modes, and surface reports in the demo and host API. **Done when:** legacy custom formats still work, discarded/approximated content is reported, cancellation remains cancellation, and malformed documents remain errors rather than successful empty imports.

- [ ] **P5.2 - Establish a cross-application corpus.** Add redistributable or locally generated Word, LibreOffice, browser, and native fixtures with provenance, application/version, expected model properties, and expected loss reports. Cover complex lists, sections, spans/nesting, typography, hyperlinks, whitespace, images, malformed data, and unsupported constructs. Define semantic comparison separately from byte equality and application-rendered visual comparison. **Done when:** every declared feature has positive/negative fixtures and every known loss has an expected diagnostic.

- [x] **P5.3 - Expand HTML and RTF mappings.** Bring HTML into alignment with list identity, richer styles, nested tables, and inline resources. Add RTF table/list/link/section support in separate increments, followed by supported image and richer typography mappings. Preserve parser limits, escaping, Unicode handling, safe-link policy, and the absence of implicit remote fetches. **Done when:** supported features survive import/export/re-import and unsupported mappings generate deterministic diagnostics, including explicit plain-text degradation where requested.

- [ ] **P5.4 - Expand DOCX fidelity.** Cover numbering definitions and restart/continuation, styles/inheritance, section content, nested tables and merge geometry, cell sizing/borders/padding, image relationships, and supported typography. Decide supported versus diagnosed behavior for constructs outside the flow model, such as page-layout-only or revision features. **Done when:** corpus round trips preserve the declared model semantics and exported documents open in the recorded Word/LibreOffice versions without repair prompts; package/XML limits and external-entity protections still pass.

- [x] **P5.5 - Preserve structure in clipboard fragments.** Introduce a versioned fragment representation and container-aware extraction/insertion for partial paragraphs, whole/partial sections, rectangular cell selections, merged/nested tables, and inline resources. Define boundary clipping, list-ID remapping, destination merging, unique ID regeneration, and older-fragment fallback. Keep native, HTML, and text preference order explicit; retain the existing revision/selection checks around asynchronous clipboard access. **Done when:** repeated paste cannot duplicate IDs, structure survives native copy/paste, fallbacks are deterministic, and cut/paste each undo atomically without deleting content after a stale/failed copy.

- [ ] **P5.6 - Verify native interchange and publish the support matrix.** Run copy/paste in both directions against selected browsers, Word/LibreOffice where available, and plain-text applications on each targeted desktop platform. Test native payload rejection, Windows HTML fragment offsets with non-ASCII content, resource transfer, and failure/fallback reporting. Update `README.md` format capabilities and `docs/QUALIFICATION.md`. **Done when:** every advertised platform/application pair has evidence or remains explicitly unqualified, and codec limitations are visible to both users and API consumers.

## Exit gate

- Native storage remains lossless for the current model, including merge backups and embedded resource descriptors/data under the selected resource policy.
- Supported interchange features pass semantic round trips and the declared application corpus; unsupported content produces actionable diagnostics.
- Streams remain caller-owned, partial-write/cancellation behavior is documented, and loading cannot overwrite intervening user edits.
- Full arbitrary RTF/DOCX compatibility is not inferred from passing a supported subset. Remaining corpus gaps stay tracked against the roadmap.

## Handoff

P5.1 and P5.2 precede codec expansion. HTML, RTF, and DOCX can then be delivered as separate increments. Share fragment transfer with Phase 6 drag/drop, and diagnostics/resource policy with Phase 7 integrations. Do not require every codec feature before starting unrelated interaction work.

## Implementation disposition

P5.1, P5.3 and P5.5 are implemented for the documented subset with regression
coverage. P5.2 has a locally authored corpus and expected-loss manifest, with
application-export/visual evidence open as INT-01. P5.4 codec work is implemented;
its Word/LibreOffice repair-free opening gate remains open as INT-02. P5.6 has
adapter/rejection/fallback tests and published qualification scope; native
bidirectional execution remains INT-02. Unchecked tasks retain their original
acceptance gates rather than equating synthetic tests with application evidence.
