# Test fixtures

`BaselineDocuments.cs` generates mixed scripts, bidi text, grapheme sequences, a long paragraph, and styled sections/lists/merged tables. IDs derive from SHA-256 names so generated fixture serialization is deterministic. Benchmarks compile the same generator and validate every input; no external downloads are required.

`native-basic.json` is the current native schema fixture for the structured document, including hidden cells and merge backups. `public-api.txt` is the captured preview public/protected reflection surface. **Do not regenerate fixtures or API baselines just to pass a failing test.** The project is unreleased and only the current native schema is supported; historical development schemas have no migration contract. `--capture-contracts PATH` on the benchmark runner is an explicit review tool: use a temporary directory and compare to the checked-in files before accepting a deliberate contract change.

CI runs seeds 7401, 1729 and 8675309 in both plain-text and structured modes, with 120 operations each. Classes cycle through insert/delete, formatting, lists, serialization, table insertion/deletion, merge/split; random offsets/text/styles vary by seed. Every committed snapshot, undo and redo is validated; content, IDs/formatting, directional selection and typing style are checked. Plain-text expected edits use independent string slicing/newline normalization and .NET grapheme boundaries. Structured operations use invariant/round-trip/history checks, not a second structural engine.

Set `TEXTALONIA_FUZZ_STEPS=2000` (up to 100000) for longer runs. A failure writes `seed-*.replay.json`, `.before.json`, and `.error.txt` to `TEXTALONIA_FAILURE_DIR` (default `artifacts/fixture-failures` relative to the test host). Replay with an **absolute** path in `TEXTALONIA_REPLAY` and the same corpus test filter. The log is the operation prefix through the failure; the before snapshot plus last operation form a one-step reproducer. Generated production GUIDs may differ across replays; operations use offsets/table order, never those GUID values. Promote any discovered defect to a small checked-in regression/reproducer and record its disposition in the baseline report before changing architecture.

```powershell
$env:TEXTALONIA_FAILURE_DIR = "$PWD/artifacts/fixture-failures"
$env:TEXTALONIA_FUZZ_STEPS = '2000'
dotnet test tests/Textalonia.Tests -c Release --filter FullyQualifiedName~BaselineCorpusTests
# On failure:
$env:TEXTALONIA_REPLAY = "$PWD/artifacts/fixture-failures/seed-7401-text.replay.json"
dotnet test tests/Textalonia.Tests -c Release --filter FullyQualifiedName~BaselineCorpusTests
Remove-Item Env:TEXTALONIA_REPLAY, Env:TEXTALONIA_FUZZ_STEPS
```

`native-rich.json` captures the deterministic `document-semantics` corpus in the
current native schema. It covers rich formatting, all four border/padding edges,
list definitions/restart, explicit font-weight precedence, row policies, nested
visible and covered cells, and nested merge backups. `NativeSchemaTests` compares
both fixtures with their reviewed generators, checks optional-property defaults,
and rejects unsupported versions and ambiguous or unknown schema members.
`--capture-contracts` writes the structured document as `native-current.json`.

`native-merge-field.json` exercises the current allowlisted merge-field payload,
including a named field, numeric format, fallback and cached label.
