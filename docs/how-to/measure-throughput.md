# Measure simulation and test throughput

[Documentation](../README.md)

Sample cached dashboard state without taking control of the colony.

Run commands from the repository root. Keep generated evidence outside commits and
preserve failed results.

The Watch clock buttons request native time and enter Manual; Pause video affects only
the camera feed. Action follow uses native presentation on supported writes and defaults
off. Test these in a disposable rendered session; a read-only preview or mocked API test
does not establish game-level acceptance. Confirm unsaved chat, project and policy
drafts survive tab changes. Inspect Priorities and Work with blocked, active and
verified goals, and confirm raw IDs stay in diagnostics.

For a non-invasive throughput sample of an existing controller:

```powershell
.venv\Scripts\python.exe scripts/dashboard_throughput.py --port 8787 --seconds 120 --output .rimgovernor/throughput-sample
```

The output directory must be new. This sends only GET requests to cached dashboard
state; it does not change speed, enable rendering, dismiss interruptions or take
control. Wall TPS includes pauses. Paused time is a sampled approximation, and
stop-reason counts count samples rather than distinct incidents. It excludes intervals
across disconnects, rewinds and session changes. A paused colony yields zero TPS; peak
speed and safety require separate isolated gameplay acceptance.
The sampler also reports cached observation age in game ticks over valid intervals;
missing or newer-than-clock observations are excluded from that age summary.

## Accelerated disposable acceptance

`deterministic_foothold.py --accelerated` and `research_acceptance.py --accelerated`
opt into supervised native Ultrafast boost. Every execution window requires a
native tick budget. Hazard probes run at most 30 game ticks apart, lease and
external-clock checks run inside accelerated frame batches, and stopping restores
the previous boost flag. Native forced slowdown, injury thresholds, pawn work and
automatic letter pauses remain in effect. Production does not enable this option.

Build a private companion with `-p:ThroughputFixture=true` using the
[Linux build properties](docker-inputs.md), then stage it into a new mod snapshot.
The fixture schedules native clock changes and an existing wild animal's Manhunter
transition at known ticks. The probe approaches wildlife through ordinary pawn
movement. Missing wildlife or a blocked route fails the prerequisite. Only the
known Ancient danger warning may be acknowledged during pawn setup/movement or
lease expiry setup, after fresh threat reads; the report retains those interruptions.
Pawn scenarios use the shared `advance_game` helper with its native identity,
letter-attribution and colonist-safety checks. Direct clock trials observe their
first interruption without filling the remaining budget.
The lease test drafts colonists through native orders to isolate lease expiry.

```powershell
python scripts/container_throughput.py --game <linux-game> --mods <private-mods> --profile <profile> --gabs <gabs-directory> --image rimgovernor-worker:my-throughput --output .rimgovernor/throughput-new --fixture --modes headless rendered suspended
```

Workers run sequentially through the existing content-addressed input cache.
Throughput workers keep writable state in a private Docker volume by default.
This avoids synchronous SQLite and recorder writes crossing Windows bind mounts;
SQLite durability and recorder flush rules are unchanged. Evidence is exported to
the requested output after the worker stops, and exported controller databases
must pass `integrity_check`. Live observation uses the published dashboard;
host-side run files appear after export. Successful exports and runs release the
volume. Failures retain the named volume in `result.json`; export failures also
retain the container for recovery. Use `--worker-storage bind` for an
explicit host-filesystem comparison. This setting applies to the throughput
launcher; other scenario launchers retain their own storage configuration.
Each publishes an automatic loopback [scenario dashboard](scenario-launcher.md)
and retains its URL in `dashboard.json`. Old images without dashboard support fail
before launch. The observer uses cached data and never advances the game.
Durable native recording defaults on, matching the standard scenario runner;
reports include recording time and counts. Use `--recording off` only for an
explicit overhead comparison, and keep the setting equal between speed/mode trials.
The worker image caches native system dependencies separately from source, so
editing a probe does not reinstall graphics libraries.
`--no-input-cache` measures direct-bind staging; `--no-build` requires an unchanged
image. Use a fresh output each time. Reports retain image/input/source hashes,
other containers, memory samples, startup/discovery/call timings, exact clock
readbacks, interrupted windows, supply scope and actual movement outcomes.
Window wall TPS includes clock-call overhead. Completed cases/minute includes
per-case loads; whole-run outcome throughput includes startup and other assertions.
Interrupted windows are never counted as completed tick-budget cases or silently
resumed to fill a target.

Rendered workers use private Xvfb/llvmpipe. Suspended workers use a renewable
test-only lease to exercise camera and map-mesh suspension on Linux, where the
Windows visibility APIs are unavailable. The fixture is absent from production
builds. Mode readbacks must confirm actual suspension/rendering. Action watch is
off. Add `capture` to `--modes` to measure rendered screenshot requests at most
once per second during active windows; PNGs and their hashes are retained.

Use `--checkpoint <native-save.rws>` to copy an unchanged older save into every
worker. Its hash and `saved_checkpoint` label distinguish reuse from fresh starts;
starting-supply assertions are skipped for reused saves. Keep the same checkpoint,
inputs and source for comparisons. Movement still needs a healthy pawn and an
eligible nearby destination.
Private profiles enable pause-on-load. Every reload must start paused at the
same saved tick (allowing the single native load tick), or the comparison fails.

Use `--runtime-seconds 120 --modes headless` to profile the production deterministic
controller instead of the bounded probe. Add `--runtime-accelerated` for its
test-only accelerated counterpart. This runs the read-only dashboard sampler
against the private controller, retaining wall TPS including pauses, controller
status, events and per-operation identity, preview, dispatch and read timings.
It asserts zero inference attempts; it measures the loop without claiming colony
survival. Use fresh output directories for both runs.
Runtime and deterministic foothold reports include `startup_milestones`: elapsed time
from automation setup to the first observed execution window, advanced tick, pawn work,
new built object and stable foothold sample. Missing milestones remain unobserved.
These reuse retained native data without extra bridge requests; timestamps are upper
bounds set by observation cadence. Pawn work requires changed position/carry during
the same work job. Neither work nor new buildings are attributed to controller orders.
Load changes or observed rewinds invalidate the timing. Select a long enough sample
to reach native progress before making setup-performance claims.
For focused scheduling acceptance through the standard scenario launcher, run
`python scripts/throughput_runtime.py --seconds 180 --accelerated --pause-race`.
This first advances 60 ticks with the shared scenario helper, then delays a retained
active-status reply until the native tick boundary has stopped the same lease. The
real pause refusal must reconcile without another pause attempt or tick advancement.
The following production sample requires observed pawn work and no stopped review or
execution. It is a targeted boundary scenario, not an unchanged-baseline benchmark.
Add `--profile-controller` to retain `controller.pstats` and completed wall timings
for persistence, review, identity, native dispatch and Hands. Timings overlap;
Python function timings include synchronous I/O and profiler overhead. Compare
unprofiled runs for throughput claims, and keep worker storage equal unless it is
the variable under test.
Use `--observation-comparison` with `--runtime-seconds` to compare four pairs
of legacy and batched observations on the same paused native state, reversing
order between pairs. Both paths must produce the same typed facts. Use
`--runtime-unbatched-observations` for the legacy path during the runtime sample.
The normal path uses the batch only when the native identity advertises version 1;
older companions retain individual reads.

Runtime reports include `bridge_calls`: request recording, shared-queue wait,
MCP session call, response model conversion, response recording and total seconds.
Native receipt `DurationMs` is retained separately. Session time includes GABS,
transport, native scheduling and response decoding; subtracting native duration
does not isolate network latency. Native batches expose an initial main-thread
queue measurement and per-section await times, including their scheduling.
Recorder statistics separate lock wait, JSON encoding, rotation, write/flush and
fsync. These nested timings overlap the bridge totals; do not add them together.
The timing callback is opt-in, stores no arguments, and cannot alter a receipt.

Observation batching retains the existing section filters, before/after ticks and
diagnostics. It does not cache facts or allow concurrent mutations; a game/map
change invalidates the batch. The game can advance between sections, so a batch
is not an atomic snapshot. Request and error durability remains unchanged; payload
encoding is reused for size checks and writing.
The recorder keeps its append handle open between events, flushes every row,
and fsyncs durable events. Rotation closes the handle before renaming segments;
session shutdown and changing recorder destinations close it durably.

Use `--placement-comparison` with `--runtime-seconds` to compare four reversed-order
pairs of 16 individual and batched native placement previews. The probe retains
complete placement verdicts, costs and footprints, including invalid candidates;
it rejects malformed/oversized requests and checks unchanged construction and
paused ticks. Native batch timing separates main-thread queue and execution time.

Use `--search-comparison` with `--runtime-seconds` to compare selected sites,
exhausted searches with explicit footprint exclusions, and material alternatives
on the same paused native state. Two pairs reverse request order and require
identical results, unchanged construction and unchanged ticks.

Use `--shell-comparison` with `--runtime-seconds` to compare the Hands read-only
shell preflight with individual versus shared observations. The probe selects a
clear observed 4x4 site, reverses order between two pairs, and retains request
counts and validation results. It requires unchanged paused ticks and construction.

Construction preflight, resource allocation and site searches prefetch at most 16
placements through `home/placement_previews` when colony identity advertises
`placementPreviewBatchVersion: 1`. The input is a validated JSON list of
definition, coordinates, rotation and material;
extra fields, non-integer coordinates and more than 16 candidates are refused.
The ordinary preview evaluator runs on one native main-thread turn. Candidates
do not project each other's buildings or
reserve materials. Results are retained only within the current review, preserving
material choice order. Alternative materials use separate bounded batches. Site
searches inspect the first candidate individually, then batch subsequent candidates
through the runtime inspection guard, retaining the first safe site in search order.
Room-shell material selection and final allocation use separate reviews.
Hands shares zone and entrance reads only within its read-only whole-shell pass,
and batches that pass's placement previews. Its final spatial preflight also
advertises the native batch capability through the guarded inspection adapter.
No shared observations pass into actual placement: every write retains its fresh
existence, preview, resource, site and identity checks. Direction, plan and load
invalidation guards run throughout the shared pass, including cache hits.
Older companions use the
individual path. Actual writes retain fresh native preflight and identity checks.
Inspect the final controller events as well as the sampler totals: a valid
measurement can include a controller stopped by a native order refusal. Such a
run measures the resulting pause; it does not establish uninterrupted progress or
an end-to-end speedup. Keep refusal evidence and compare it with the ordinary run.

Measure performance without competing workers. Resource snapshots expose
contention; correctness passes under contention do not establish isolated
throughput. Retain failed prerequisites and infrastructure errors alongside passes.
Bounded movement and scheduled hazards do not establish sustained colony survival
or arbitrary high-speed safety. Existing 600/3000-tick review windows remain;
larger adaptive windows need separate observation-age acceptance.

## Local inference throughput

`scripts/inference_throughput.py --output <new-directory>` compares one and two
concurrent local requests against the same scored semantic cases. It defaults to
two rounds with reversed concurrency order on the second, retaining startup costs
and every response. It defaults to Qwen 3.5 4B; select an installed model with `--model`. In a local Docker image, set
`RIMGOVERNOR_ALLOW_DOCKER_HOST_MODEL=1` and pass
`--model-url http://host.docker.internal:1234/v1`. Reports retain settings, fixture
hashes, responses, failures, token counts and correct requests/minute. No game
orders are sent. Hold model weights, context/offload settings and competing
inference constant; a small timing sample is not a reliability guarantee.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
