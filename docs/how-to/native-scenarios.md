# Run and diagnose named native scenarios

[Documentation](../README.md)

Use local Linux Docker and [prepared inputs](docker-inputs.md). Run from an isolated
task worktree with a private, fully staged mod snapshot. No scenario calls a language
model. Native game files stay outside the image.
Rebuild the private observation/identity mod with the task source: disposable launches
use `-rimbot-pause-on-load` to pause in the native loaded-game callback. The startup and
construction probes require the exact saved tick, not a later controller pause.

```powershell
python scripts/native_scenarios.py --list
python scripts/native_scenarios.py --scenario construction production paired-restart --game <linux-game> --mods <private-mods> --profile <profile> --gabs <linux-gabs-directory> --image rimbot-worker:my-task --output .rimbot/scenarios-01
```

The default is one container at a time, with two CPUs, 4 GiB memory and a 900-second
wall deadline. `--workers` permits up to four simultaneous containers. `--repeat`
creates fresh trials and preserves every failure in the aggregate and JUnit results;
it never retries an ambiguous native write. `--gc source` preserves Unity's original
boot setting; the default `--gc 0` selects the recorded startup mitigation. Use
`--no-build` only with an unchanged pinned image.
Each worker publishes an automatic loopback dashboard port through the shared
[scenario launcher](scenario-launcher.md) helpers. Images without its dashboard
hook are rejected; rebuild them before running scenarios.

The default `--storage volume` keeps live runtime claims, saves and SQLite files on
a private Linux Docker volume. After stopping the owned container, the host exports
the complete volume into the trial directory before classifying results or backing up
SQLite. A failed export retains the named volume and reports an infrastructure
failure; `trial.json` identifies it for recovery. Container logs and resource samples
remain visible on the host during execution. `--storage bind` retains the Windows
bind-mount mode for explicit filesystem comparisons and reproducing publication races.

`--display xvfb` runs the same cases with a private software-rendered display. The
shared construction/failure probe retains a native frame when the game remains
available. `--recording off` is for explicit overhead comparisons; those trials report
the missing timeline instead of claiming recorder coverage.
`--gabs-log-level debug` retains GABS lifecycle/claim diagnostics in the container log;
the default is `info`. Reads may recover from the two recognized runtime-publication
races through the shared bounded read helper. Mutations are never retried by that helper.
Fresh startup waits for GABS's existing background connector to publish tools; it
does not open a competing connection or relaunch a failed native process.

Construction requires a separately observed built wall and consumed material after
ordinary scoped starting-supply setup. `blocked-construction` disables construction
through normal work settings and checks that accepted orders cannot satisfy completion.
Production reuses the native bill, output and consumption probe; `acquisition` also
requires observed safe surface mining and harvest sources. A baseline containing only
roof-adjacent or protected ore is a missing prerequisite. `plant-acquisition` selects
only herbal medicine and wood. `mining` uses the separate
[surface mining fixture](mining-acceptance.md), including native hauling, roof refusal,
cancellation and pending-dig restart. Paired restart reuses the mixed pending
work/checkpoint probe. The registry lists each scenario's fixture prerequisites.
Acquisition replans from fresh native sources while an observed stock deficit remains;
estimated yield is not credited as inventory. A trial never issues the same source
identity twice and bounds the number of selected sources.
`scarce-supplies` checks ordinary prewrite recovery. `placement-obstruction` requires
pawns to build a wooden wall across a proposed campfire footprint, verifies
refusal, and observes ordinary deconstruction before recovering the campfire order. It does not certify pawn
pathfinding around a blocked route. `player-edits`
checks native edits, dependencies and save rewinds; `queued-construction` requires
scarce-stock scheduling followed by observed dependent pawn completion and a private
companion build with `ConstructionLedgerFixture=true`.
`projected-access` reuses native spatial preflight to reject proposed walls that
would seal an access pocket. It checks a proposed obstruction, not a trapped pawn.
`treatment` verifies the existing native combat/tending recovery fixture in headless
mode. `video-input` requires Xvfb and checks one minute of decoded video plus native
click and shift-drag selection. Its right-click observation does not certify menus.
`rendered-input` adds native Work-tab, context-menu, stockpile-drag and camera-edge
readbacks. These exercise live native play-UI tools; they do not measure browser
coordinate mapping or end-to-end browser latency.
`endurance` requires thirty exact, paused 6,000-tick boundaries over three game days;
it allows ordinary starting supplies and records window timings. The disposable
fixture uses `rimbot.native_scenario.advance_game` for exact remaining-tick continuation
after attributed Ancient danger warnings and complete native safety observations.
Unexpected holds fail. This measures native
process/clock endurance, not successful colony survival or completed pawn work.
These cases do not imply acceptance of every seed, path obstruction,
rendering or local-model interpretation.

Every trial retains its container log, native logs, input hashes, timeline and result.
The host manifest records source hashes, Git revision, immutable image ID, parameters
and a rerun argument vector with a fresh output placeholder. Successful exit alone
cannot establish acceptance: the probe must record a successful final outcome too.
Categories distinguish missing prerequisites, infrastructure, assertions and timeouts.
`resources.jsonl` samples Docker CPU/memory/block I/O; the result includes retained
disk bytes. Those samples are not a precise native GC-pause or peak-memory profiler.
Per-trial resource summaries report sampled peak memory and mean CPU; concurrent
host workloads prevent interpreting these as isolated benchmark measurements.
Original-setting failures and gameplay interruptions remain separate categories in
the retained evidence. The GC-zero setting is a startup mitigation, not an engine
repair; sampled throughput cannot establish precise GC pause behavior.

`warning-recovery`, `warning-pause`, `warning-modal`, `warning-raid` and `warning-load`
exercise shared-wait recovery and refusal through native callbacks. They require the
separate `scripts/fixtures/InterruptionFixtures.csproj` assembly under the private
mod snapshot's `RimBotObservations/BridgeTools/InterruptionFixtures/` directory.
Build with the Linux `RimWorldManagedDir` and `RimBridgeSdkDir` overrides. The fixture
delivers native letters, opens a real modal and executes an ordinary raid incident;
the pause case uses a native player-equivalent call, not physical keyboard input.
These setup tools remain outside gameplay capabilities and production mod builds.

For targeted continuation, select only `checkpoint-continuation` and add
`--checkpoint <retained-checkpoint-directory>` to the named-runner command. Supply
the same game/mod inputs and display mode. It accepts immutable owned checkpoints
from `/worker/scenario/bridge`, copies the verified pair into a fresh worker, resumes
into a new database, and observes previously issued construction to completion without
redispatch. The original manifest and hashes remain unchanged. Reports label this
separately from fresh-start acceptance; other checkpoint workflows use
[save and resume](save-and-resume.md).
`construction-checkpoint` creates such a pair before pawn work and then verifies fresh
construction completion. A restored journal shorter than the saved controller cursor
emits an explicit durable history gap and invalidates pending direction in Manual;
it never sends an out-of-range native read or silently skips the discontinuity.

## Diagnose without the live game

```powershell
python scripts/inspect_native_failure.py .rimbot/scenarios-01/construction-1
python scripts/inspect_native_failure.py .rimbot/scenarios-01/construction-1 --summary
python scripts/inspect_native_failure.py .rimbot/scenarios-01/construction-1 --export .rimbot/construction-fixtures.json
```

Offline inspection uses only Python's standard library. Each trial also writes
`diagnosis.json` with available postconditions, last tick/resources/jobs, evidence
paths and explicit timeline gaps; a diagnostic failure is retained in the trial result.

The opt-in recorder writes native requests durably before transport dispatch, followed
by responses or exceptions. Successful responses flush without a separate disk sync;
the next durable request, error or outcome also syncs prior responses. A host crash
can therefore leave a reported unmatched request. Rotation syncs the outgoing segment.
Runtime persistence records plan/action state; native
requests carry current runtime identity, direction/plan revision and the last observed
tick. Planned dispatch also carries its action and goal IDs in an asynchronous call
context; unrelated background calls do not inherit that scope. Response payloads retain native operation identities and readbacks. The recorder
does not observe every in-game pawn transition or model token. Payloads above 256 KiB
are explicitly truncated and hashed; eight approximately 8 MiB segments bound retention.
Truncated receipts retain their request correlation, while fixture export excludes
their incomplete payloads.
Offline inspection reports discarded prefixes, sequence gaps and corrupt tails. Recorder
statistics expose time spent recording, rotations and truncation counts.

The host stops only its named container and takes SQLite backups using SQLite's backup
API. It retains stop/cleanup errors and original files. The construction/failure probe
attempts a paired failure checkpoint only after a native read verifies a paused game;
missing checkpoints retain their reason. A dead game is never restarted to invent a
checkpoint. Headless scenarios have no rendered frame. Use `video-input` for decoded
video evidence and the separate rendered acceptance runner for camera lease/control checks.

`assertion-failure`, `timeout` and `native-exit` deliberately produce failing reports
for bundle validation. They are not successful gameplay runs. Native-exit stops the
owned game through GABS and exercises lost-game diagnosis; it does not emulate a Mono
segmentation fault. Export includes only complete matched requests/responses. Feed those
recorded values into focused controller fixtures, then rerun the original native
scenario to verify a fix. Fixture export is not a native simulation replay.
