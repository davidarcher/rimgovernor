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
.venv\Scripts\python.exe scripts/dashboard_throughput.py --port 8787 --seconds 120 --output .rimbot/throughput-sample
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
python scripts/container_throughput.py --game <linux-game> --mods <private-mods> --profile <profile> --gabs <gabs-directory> --image rimbot-worker:my-throughput --output .rimbot/throughput-new --fixture --modes headless rendered suspended
```

Workers run sequentially through the existing content-addressed input cache.
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
`RIMBOT_ALLOW_DOCKER_HOST_MODEL=1` and pass
`--model-url http://host.docker.internal:1234/v1`. Reports retain settings, fixture
hashes, responses, failures, token counts and correct requests/minute. No game
orders are sent. Hold model weights, context/offload settings and competing
inference constant; a small timing sample is not a reliability guarantee.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
