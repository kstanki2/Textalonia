# Phase 4 text accessibility

The managed range contract is implemented. Complete native text accessibility remains blocked by the pinned Avalonia provider/bridge API and unexecuted native screen-reader qualification.

## Verified dependency

`Directory.Build.props` pins Avalonia **12.1.3**. The restored NuGet package's `avalonia.nuspec` identifies source commit `8eeda4f6f546165b3f72e63c9f42247abb306905` on `release/12.1.3`. Its `lib/net8.0/Avalonia.Controls.xml` documents `IValueProvider`, selection-item and selection-container providers, but no text or text-range provider. `AccessibilityTests.Pinned_Avalonia_public_assembly_has_no_text_pattern_contract` checks the actual compiled assembly for `Avalonia.Automation.Provider.ITextProvider` and `ITextRangeProvider` and confirms their absence. Selection-item/container providers describe selected child controls, not offsets or text ranges.

The surface therefore retains Avalonia's supported `IValueProvider`, and exposes Textalonia's `DocumentTextProvider` through `editor.Accessibility` and the peer's managed `GetProvider<DocumentTextProvider>()`. This does **not** register a Windows UIA TextPattern, macOS AX text interface, or Linux AT-SPI Text interface. A host/native bridge must explicitly adapt it. `HasNativeTextPattern` is consequently false. Do not label the value provider as complete text accessibility.

## Managed contract

- Ranges use committed, normalized UTF-16 document coordinates, including one U+FFFC for each inline object and one newline between indexed paragraphs. `GetDescriptions` supplies inline alternative text and table/cell context separately, preserving coordinates. Missing images and unregistered controls retain their descriptor descriptions.
- Character navigation uses the editing session's grapheme boundaries. Word navigation follows the session's existing whitespace policy. Paragraph and document navigation use the document index; line navigation uses shared shaped layout. These are logical text units, not visual bidi arrow navigation.
- Document, selection and caret ranges, selection direction, caret activity, read-only state, bounded text reads, endpoint navigation, range expansion and selection are available. Read-only permits selection. Selection cancels transient composition, just like normal navigation.
- Ranges are immutable and valid for their originating session revision. Text edits, formatting changes, load, undo and redo invalidate them. Every operation rejects stale ranges with `InvalidOperationException`; clients reacquire a range after a change. Ranges retain the provider, never historical snapshots.
- Bounds are surface-local DIPs. Point queries use the same coordinate space. Bridges must convert to/from native screen coordinates with the current scale and scroll transform. Offscreen queries resolve only intersecting pages through the bounded layout cache. A rectangle-count limit defaults to 1024 and throws rather than silently truncating; query smaller ranges for large documents.
- Scroll requests resolve target geometry and publish extent changes before scrolling. They preserve selection. Visible ranges identify shaped page portions intersecting the viewport, and may extend beyond its exact top/bottom line.
- Geometry and line navigation require an attached template and visual root. Geometry queries during transient preedit explicitly fail instead of confusing composition coordinates with committed text. Committed text/ranges stay readable during preedit. Rendering-limit rejection is propagated as `ShapingLimitExceededException` rather than approximate geometry.
- `Changed` distinguishes document revision, selection (including direction) and read-only changes without full-document text materialization. Native value property change notifications are produced after a native client first requests the complete value. Native text-selection/caret notifications require the missing bridge. Focus is available through `IsCaretActive` and Avalonia's normal focus automation.
- All managed range operations run on the editor's UI thread. A host bridge must dispatch its calls there. Providers follow editor ownership; template replacement does not retain the old surface.

## Evidence and remaining native gate

Run the managed contract suite with:

```powershell
dotnet test tests/Textalonia.Tests --no-restore -p:UsedAvaloniaProducts= --filter FullyQualifiedName~AccessibilityTests
```

The tests cover the pinned API limitation, grapheme/word/line/paragraph ranges, directional selection/caret, read-only behavior, stale revisions, nonmaterializing events, actual value-peer notifications, offscreen virtualized bounds/scroll, point queries, composition isolation, and inline/table descriptions without a visual factory.

| Platform | Provider/bridge evidence | Native screen-reader evidence | Gate |
| --- | --- | --- | --- |
| Windows | Managed value pattern and range contract tested; Avalonia public text-pattern interface absent | N07 pending; no native screen-reader operator/tool available in this environment | UIA text-range bridge plus NVDA/Narrator run |
| macOS | Same pinned managed API limitation | N07 pending; macOS host unavailable | AX text bridge plus VoiceOver run |
| Linux | Same pinned managed API limitation; no AT-SPI backend qualification performed | N07 pending; Linux desktop host unavailable | AT-SPI Text bridge/backend support plus Orca run |

Run [N07](NATIVE-BASELINES.md#n07--screen-reader-value-and-selection-announcements) on each supported native configuration. Record value reading separately from text-range navigation, reverse selection/caret announcements, inline alternative text, table coordinates, scroll-to-offscreen range, and DPI-correct bounds. Use the [native evidence records](baselines/native); no pending record is converted to a pass by headless tests. Full P4.6 native completion remains blocked until a backend adapter exists and these native runs pass.
