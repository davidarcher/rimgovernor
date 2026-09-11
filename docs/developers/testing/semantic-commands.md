# Verify semantic player commands

[Documentation](../../README.md)

Measure fixed-fact model interpretation separately from real native command acceptance.

[Native test prerequisites](README.md#native-test-prerequisites).

## Live planner probe

Launch a fresh disposable colony with an empty plan and leave it in Manual:

```powershell
.venv\Scripts\python.exe scripts\live_planner_probe.py --seconds 180
```

This enables automation and real model orders, records results in
`.rimgovernor/live-planner-probe.json`, then returns the same session to Manual. It does not
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

## Expanded native and model matrix

Add `--matrix` to verify two available and two locked research projects before
and after `--restart`, plus maintained resource-goal cancellation/resumption.
Refusal requires the model to request the intended project and receive its native
refusal; merely leaving research unchanged does not pass that case.
Add `--archive` to execute 81 explicit zero-inference policy orders, verify exact
archived PLAYER receipts and retained intent, then accept new chat. With `--restart`,
the probe also verifies those receipts after paired native save/load.
`--explicit-resources` disambiguates ordinary versus advanced components in the
initial policy request; retain the broad-wording failures separately.

The benchmark has 32 fixed cases. `--repeats 2` covers fresh and historical-message
contexts. Wrong-resource, reserve-tool and unauthorized-removal counts are separate
schema-valid error categories; schema and request failures are counted independently.
Run the same matrix on each local model and retain all failed cases.

The chat and refinement probes accept `--model-url`, defaulting to `RIMGOVERNOR_MODEL_URL`
or loopback. Docker workers explicitly permit the local Docker host address.
Worker images set `RIMGOVERNOR_CONTAINER_SOURCE=1`: native manifests hash packaged source
bytes when Git metadata is absent and record the Git revision as unavailable.
Use a fixed image and current private companion DLLs. An unbuilt controller bind
mount hides the image's dashboard assets. Linux-local runtime storage can avoid
Windows bind-mount publication faults; export its entire evidence tree before
removing the owned container.

## Related reading

[Testing](README.md) · [Backlog](../../BACKLOG.md)
