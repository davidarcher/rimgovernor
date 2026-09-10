# Verify semantic player commands

[Documentation](../README.md)

Measure fixed-fact model interpretation separately from real native command acceptance.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

## Live planner probe

Launch a fresh disposable colony with an empty plan and leave it in Manual:

```powershell
.venv\Scripts\python.exe scripts\live_planner_probe.py --seconds 180
```

This enables automation and real model orders, records results in
`.rimbot/live-planner-probe.json`, then returns the same session to Manual. It does not
reload a save or reset an existing plan. Compare from the same fixture and fixed
revision/model settings. `orders_observed` proves neither completed shelter nor
survival.

## Semantic commands

`scripts/semantic_command_benchmark.py --output <fresh-directory>` measures the
configured local model against fixed controller facts with no game writes. It compares
complete typed requests, including default reserves, and separates schema failures,
schema-valid semantic errors, extra calls and request failures. Research labels may
resolve only to their exact observed fixture definitions; goal aliases use the shared
goal resolver. Cases cover research, food targets, policy reserves, cancellation and
goal resumption. The explanation case checks the fixture's steel amounts and absence of
writes; this is a limited text check, not a general measure of answer quality. A single
passing run is not a reliability rate. Add `--model <local-model-id> --repeats 3` to
repeat the fixed cases, including combined spending/reserve changes and multi-resource
requests. Complete typed request multisets must match; duplicates, omitted calls and
unrequested fields fail. The manifest preserves settings and a case/fact/tool
fingerprint. Per-case rates and Wilson intervals describe this fixed benchmark, not
arbitrary commands. `scripts/interactive_commands_probe.py --source-root <prepared-root>
--output <fresh-directory>` runs real chat requests in a private paused colony, then
verifies a work assignment, a persistent food target and a component policy. The probe
uses the normal shared validator and Hands. Preserve failed responses; fixed-fact model
correctness and native command acceptance are separate results.

Add `--extended --model qwen3.5-4b --rendered --port 8788` to watch the disposable
colony in its own dashboard while testing research selection, locked-project refusal,
food-goal cancellation, two autonomous reviews and explicit goal resumption. Research
candidates come from native available/locked catalogs. The probe requires the requested
current-project readback and completed PLAYER action; locked research must preserve the
selected project without adding an action. Cancelled food work must remain suppressed
across the two reviews, which must not invoke a model. The basic checks also reject
unrelated work changes and unrequested component reserves. Extended checks explicitly
set a component reserve, change spending while retaining that reserve, and clear the
reserve without changing spending. All interactive orders start in Manual; the two
cancellation checks temporarily enable routine autonomous operation in this disposable
colony only. The probe stops its owned game/server afterward and retains results and
failures.

Extended checks also combine a food target with steel reserves and component spending,
then change both resource policies while preserving reserves. They reject unrequested
advanced-component policies and extra native actions. Add `--restart` without `--port`
to save and stop the owned game, resume the paired controller/native checkpoint, compare
the complete plan and conversation, and exercise goal cancellation and resumption
afterward. A passed paused-session check does not prove pawn production. The semantic
benchmark includes native labels for related resources as distractors; score exact
resolved definitions rather than accepting extra resource changes.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
