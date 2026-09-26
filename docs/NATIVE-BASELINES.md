# Native behavior baseline scripts

These manual scripts test native OS integration using the desktop demo. Headless injection tests are supporting evidence only. The initial [records](baselines/native) are **pending**, including Windows: this execution environment exposes no native desktop input/screen-reader automation or human operator. macOS/Linux hosts are unavailable. The [qualification matrix](QUALIFICATION.md) assigns an execution owner to each target. None is certified.

## Prepare a run

```powershell
dotnet restore Textalonia.sln --configfile NuGet.Config
dotnet build Textalonia.sln -c Release --no-restore
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --export-fixtures artifacts/native/fixtures
pwsh -File scripts/New-NativeRun.ps1 -Target windows -Owner 'Windows QA maintainer'
dotnet run --project samples/Textalonia.Demo -c Release --no-build
```

Use `macos`/`linux` for the other targets. `New-NativeRun.ps1` writes an inventory/checklist, never a pass. Fill exact OS build, native backend/version, engine commit, IME and input mode, application builds, screen-reader version and output-device geometry. Use Inter at 16 DIP and list all fallback fonts actually used (CJK/Arabic/emoji); record font package versions. Set the window to 1000 x 700 DIP, repeat at 700 x 500, Light then Dark. Windows: 96 and 144 DPI; macOS: standard and Retina backing scale; Linux: 1x and 1.5x if supported by that desktop, with X11/Wayland explicitly recorded. Repeat on each advertised configuration rather than merging unlike runs.

Open generated `.textalonia` files with the demo's Open button. Before destructive steps, use a fresh fixture; discard changes only to that scratch demo document. `Ctrl` below means `Cmd` on macOS for editing shortcuts. Record a short screen capture, observed text/selection, timing/geometry notes and fixture saved after the case. Keep evidence paths next to the JSON. Set `status` to `pass`, `fail`, or `pending`, with execution time. A fail needs a reproducer and corrective task; missing evidence stays pending. N07 value and selection must be reported separately in the reason/evidence even when the combined case fails.

## N01 — CJK composition and commit

Open a blank scratch document, type `before `, move Left then Right to break the typing group. Enable each configured Chinese/Japanese/Korean IME in turn. Compose and choose the candidates **中文**, **日本**, **한글** respectively (use the IME candidate list, not paste). During preedit, characters should appear at the caret with composition feedback while committed document text/count remains `before `. Commit once. Expect exactly `before 中文` / `before 日本` / `before 한글`, one insertion, no duplicate characters. Undo once must restore `before ` and its caret; redo restores the committed result. Repeat replacing a reverse selection in `mixed-scripts.textalonia`. Preserve the selection direction on undo. Corrective owner: input maintainer, P4.2/P6.6.

## N02 — Composition cancellation

Start N01 preedit without committing. Escape must remove transient text and leave the committed document/caret unchanged, with no new undo step. Repeat by moving focus out of the editor, toggling read-only, and opening `emoji.textalonia` while composing. No preedit may leak into a replacement document; no committed text may appear after entering read-only. Record whether the platform consumes Escape or requires its native cancel key. Replacement intentionally resets history; do not apply the unchanged-history assertion to the newly loaded file. Corrective owner: input maintainer, P4.2/P6.6.

## N03 — Candidate-window placement after scroll/resize

Open `scroll.textalonia`. Click a visible character around paragraph 50 after scrolling. Compose without committing, then scroll another viewport and reposition the caret before composing again; resize to 700 x 500 and repeat at the second scale. The composition caret must overlay the intended text position. The candidate window must track that caret in **screen coordinates**, adjacent to its line (or flipped above by the OS at a screen edge), never at the old scroll position or with a duplicated DPI offset. Record caret/candidate rectangles in screenshots and note OS-managed offsets. Commit at the new location and undo once. Corrective owner: layout/input maintainers, P2.4/P6.6. Headless cursor height assertions do not pass this case.

## N04 — Dead keys and combining characters

Use US-International acute + `e` (ABC Extended equivalent on macOS) to produce `é`, then type `x`. Expect `éx`, no literal apostrophe and no lost key. Move Left then Backspace: remove the entire `é`, leaving `x`; undo restores it. Also use `emoji.textalonia`: remove `e` plus its combining acute and the woman-technologist ZWJ sequence one grapheme at a time. Undo restores exact text/caret. Record whether the IME emits precomposed or decomposed accent text; both must behave as one grapheme. Corrective owner: input maintainer, P6.6.

## N05 — Mixed RTL/LTR navigation

Open `mixed-scripts.textalonia`, select its Arabic/Hebrew paragraph, and repeat in a table cell after copying that paragraph. Move with Left/Right, Shift+Left/Right, Home/End and Up/Down through Latin, Arabic/Hebrew, numbers and punctuation at both widths. Record the UTF-16 anchor/active direction by saving a selection note and the visual caret sequence in a screen capture. Desired behavior: visual arrows follow adjacent shaped caret stops; reverse selections preserve their fixed anchor; deletion removes a complete logical grapheme; vertical movement preserves the visual column. **Current known gap:** arrows use logical positions, not shaped visual bidi stops; any mismatch is a P6.1 failure, not a baseline pass or certification. Cross-platform actual results remain pending until executed.

## N06 — Native rich clipboard

Open `structured.textalonia`; select the styled paragraph (bold, italic/underline link, colored text and soft break), copy into a second demo and paste back. Expect exact visible text, run emphasis, link target, colors and U+2028; one undo removes the paste. Open [`clipboard-source.html`](baselines/clipboard-source.html) in the named browser and copy its **rendered** paragraphs into the demo; copy the same content through the named word processor in both directions. Record application builds, screenshots and saved native result. Expect supported run formatting and paragraphs; do not claim unsupported external-format fidelity. Tables/sections flatten in the current rich fragment: record this as P5.5 scope limitation, not structure-preservation success. In read-only mode copy must work and cut/paste must not modify the document. Corrective owner: interchange maintainer, P5.5/P5.6.

## N07 — Screen-reader value and selection announcements

Start the target screen reader, focus the editor and use its current-value/read control command. Expected value contract: announce an editable text control named Textalonia editor with visible document text; in read-only mode expose read-only state and allow reading. Type a word, reverse-select it and move the caret between paragraphs. Desired text-accessibility contract: announce changed value, selection text/direction and caret context; allow character/word/line navigation and query offscreen range bounds. Save speech viewer output/transcript with action timestamps. **Known Phase 4 gap:** the editor has a tested managed text-range contract, but the native peer implements only `IValueProvider`; Avalonia 12.1.3 lacks the public native text-provider bridge. See [verified dependency and platform gates](PHASE4-ACCESSIBILITY.md). Value availability does not imply selection announcements. Track missing announcements and backend bridges against P4.6, and repeat after P6.6 integration before advertising accessibility.

## Evidence disposition

| Case | Existing automated support | Native status on Windows/macOS/Linux | Corrective task |
| --- | --- | --- | --- |
| N01 | `Ime_preedit_is_transient_and_commits_as_one_edit` | Pending / pending / pending | P4.2/P6.6 |
| N02 | `Replacing_document_or_entering_readonly_cancels_preedit` | Pending / pending / pending | P4.2/P6.6 |
| N03 | IME rectangle nonzero in headless test only | Pending / pending / pending | P2.4/P6.6 |
| N04 | Grapheme model and real-control input tests | Pending / pending / pending | P6.6 |
| N05 | Logical grapheme selection; visual bidi missing | Pending / pending / pending | P6.1 |
| N06 | Headless in-process rich clipboard round trip | Pending / pending / pending | P5.5/P5.6 |
| N07 | Managed text-range contract tested; native text-provider bridge missing | Pending / pending / pending | P4.6 |

CI uploads the checked-in pending records along with tests and generated fixtures; it does not overwrite them with headless passes. A platform owner may append dated evidence files and update this table after an actual native run. The absence of native input tools in this session is an execution limitation, not evidence of a product failure.
