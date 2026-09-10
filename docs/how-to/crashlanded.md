# Verify ordinary colony establishment

[Documentation](../README.md)

Exercise shelter handoff, starting populations and stability using ordinary native
colonies.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimgovernor/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

`scripts/shelter_handoff_acceptance.py --source-root <prepared-root> --output
<fresh-directory> --seconds 600` permits observed starting supplies through the
production compiler/Hands and requests a player shelter shell. Ordinary pawn labor must
complete it, after which the deterministic controller furnishes sleeping places in that
roofed native room without issuing an autonomous shell. The report retains native rooms,
player progress and zero-inference counters. This is bounded sleeping handoff
acceptance, not food survival or complete adopted-room furnishing. Add `--services` to
request a different observed site while retaining the old starter cache. Completion also
requires a native campfire, active cooking bill and nine food-storage cells inside the
adopted room. The report preserves the source/input manifest, blocked trials and
zero-inference counters. This does not certify sustained food replacement or
heating/cooling under temperature extremes.

Prepare a fresh isolated native start, then run the production controller:

```powershell
python scripts/prepare_crashlanded.py --source-root <prepared-bridge-root> --output <fresh-bridge-root>
python scripts/deterministic_foothold.py --source-root <fresh-bridge-root> --output <fresh-report-directory> --seconds 1800 --speed Superfast
```

The preparer discovers `rimworld/start_debug_game_ready`. That native lifecycle entry
uses the ordinary Crashlanded scenario, Cassandra/Rough and normal world/ pawn
generation. It waits for the three starting colonists to arrive, pauses and uses native
SaveGame; it does not alter pawn stats, needs, supplies or save XML. Its report records
the initial tick and save provenance. The inherited baseline filename still says
`tribal8`; the saved scenario and observed colonist count are the authority. Generate
another isolated root for another random map seed.

## Configurable populations

For a configurable starting population, build the companion with
`-p:ScenarioStartFixture=true` and temporarily install it while every game is closed.
Run `scripts/prepare_scenario.py --source-root <prepared-bridge-root> --output
<fresh-bridge-root> --scenario Crashlanded --count 10 --seed <world-seed>`. The
test-only `test/list_start_scenarios` discovers other native ScenarioDef names. The
fixture copies the chosen scenario, applies the native editor's 1..10 pawn count and
runs ordinary world/pawn generation. It leaves supplies and pawn stats to that scenario.
World seed alone does not promise identical pawn rolls or starting tiles across
preparations. The report records the installed fixture hash, roster, unchanged global
definitions and an unchanged native save hash. It also verifies that setup refuses an
existing colony. Failed trials are retained. After preparation stops, rebuild without
the fixture flag and install the production companion before running gameplay acceptance
on that saved baseline. Restore the original installed DLL after testing. The fixture is
excluded from normal builds and the controller's gameplay capability surface.

## Work and capacity audits

Run `scripts/work_batch_audit.py --report <foothold-result.json> --output <audit.json>`
to verify a larger colony's work-setting batches. It requires more than eight starting
colonists and changed pawns, a full eight-action batch followed by another batch, native
per-setting readbacks, an observed work-coverage gate and no inference. It does not
certify pawn labor, a mid-campaign joining event or a sustained foothold; the audit
retains the source report hash and the campaign's separate outcome.

Run `scripts/shelter_capacity_audit.py --report <foothold-result.json> --output
<audit.json>` for a larger starter on fragmented soil. It requires completed native
shell and sleeping actions, enough observed indoor sleeping capacity for the starting
population, smaller accepted field patches and an observed production gate. Its scope
excludes actual bed use, crop harvest replacement and sustained survival.

The runner rejects fresh baselines past tick 600 and injects a model client that fails
on any attempted inference. By default it requires two consecutive game days with all
eleven gates verified and reports `SUSTAINED_FOOTHOLD`. Use `--stability-days 0` for
establishment-only `FOOTHOLD_STABLE`, or an explicit number up to 30 for another
duration. `--seconds` bounds the whole episode.

## Lifecycle and join scenarios

Add `--lifecycle-days 2` to measure a bounded native lifecycle campaign separately from
food-gate acceptance. The report samples real pawn `inBed`/`bedThingId`, exact live
action identities and receipts, goal evidence bytes, immutable archive/event counts,
SQLite/WAL/page growth and indexed recent-history latency. Native autosave long-event,
recovery and execution-boundary events retain the contemporaneous plan and deadline.
`LIFECYCLE_WINDOW` means the requested native duration completed with the original
colonists alive and no inference; it does not certify all food gates, bed use by every
pawn, joining events or arbitrary long-term survival. Evaluate those readbacks
explicitly against the relevant acceptance checklist. With the separate interruption
fixture installed, add `--join-count 3` to request ordinary WandererJoin incidents after
tick 50000. Each request first requires the native incident worker's `CanFireNow`, then
calls its usual `TryExecute`; failed eligibility fails the probe instead of forcing pawn
creation. The report retains the original and joined roster IDs. This test-only scenario
setup is excluded from the gameplay gateway and does not edit pawn stats or saves.

Stability uses the native facts' tick, not wall time or a newer clock reading. A
recorded stability loss resets the window even when recovery occurs between report
samples. Unknown/failed gates and observation gaps over 6,000 ticks also reset it.
Save/load identity changes, rewinds and missing/dead starting colonists fail the
episode. Reports preserve losses, the longest qualifying interval and maximum
observation gap. This certifies sampled maintained gates over the stated window, not
arbitrary long-term survival or difficult-biome coverage.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
