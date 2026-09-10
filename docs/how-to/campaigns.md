# Run campaigns and measure performance

[Documentation](../README.md)

Run repeated or sustained trials against a fixed revision and baseline, retaining failed
attempts.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

For the production deterministic bootstrap, run:

```powershell
$env:PYTHONPATH='controller'
.venv\Scripts\python.exe scripts\deterministic_foothold.py --source-root .rimbot/bridge --output .rimbot/deterministic-new --seconds 1800 --speed Superfast
```

The default is a fresh isolated headless profile; add `--rendered` for a visible game.
The current baseline is the prepared eight-tribal save. No save edits or inference are
permitted. `--speed` selects ordinary native Normal/Fast/Superfast. The optional
`--accelerated` test flag enables bounded supervised Ultrafast boost; see
[throughput measurement](measure-throughput.md). The harness runs production
BridgeRuntime, controller, validation and Hands,
writes a manifest, incremental `progress.json` and final `result.json`, and exits
successfully only for all FOOTHOLD_STABLE predicates. Results include
goal/method/lifecycle evidence, attempted and completed model-call counts and game
ticks. `--checkpoint <native-save.rws>` copies an unmodified save for targeted
debugging; those reports are labelled `saved_checkpoint` and do not count as
fresh-colony acceptance. A timeout, blocker or partial shelter is not a pass. Stop uses
this profile's PID-owned GABS launch; never terminate all processes by executable name.

For packaged Docker sources without Git metadata, add `--source-snapshot` to hash
the packaged source bytes. Unavailable Git revision and dirty flags stay unknown.
Sustained samples retain native food stocks, holders, rot forecasts, crop labor
and cooking bills.
With a private `FoodObservationFixture=true` build, add `--food-observer` and
`--food-target-days 7` to require native crop harvests at least one game day apart,
an observed seven-day accessible stock runway, and the usual sustained foothold
window. The requested goal and policy target must remain intact throughout.
`food-observer.json` retains actual native harvest/recipe products and ingestion;
`food-acceptance.json` records the separate food assertions. Projected yields do
not satisfy them. A resumed save remains targeted checkpoint acceptance.
For crop-focused acceptance, `--disable-hunting` issues ordinary persistent player
work settings and verifies native hunting priorities are zero. This isolates crop
replenishment and does not certify mixed hunting/crop autonomy. Unsafe native hunt
routes still pause and hand control back to the player in the separate mixed run.
For an isolated food-production scenario, `prepare_scenario.py --difficulty Peaceful`
selects the native preset before generation. The preparation report retains the
preset and its crop yield factor. Peaceful's ordinary yield bonus and reduced
threats limit that acceptance scope; it does not certify Rough survival.

Controller replays in `test_colony_controller.py` separately exercise deterministic
layout variants, hysteresis, priorities, cancellation and accounting. They model labor
explicitly and cannot substitute for native gameplay acceptance. Chat tests cover typed
direct orders, maintained goals, policies, follow-ups, provenance, Manual dispatch and
stale-direction rejection. Repeat native acceptance at a fixed committed revision and
restored baseline; iterative debugging runs do not establish repeatability. Keep
temporary binaries, saves and logs outside Git.

## Broader deterministic campaigns

For broader deterministic starts, build a private companion with
`ScenarioStartFixture=true` and `FoodObservationFixture=true`, then run this command
through the [standard scenario launcher](scenario-launcher.md):

```text
python scripts/foothold_campaign.py --source-root /worker/run --output /worker/trial --seed b04-temperate-01 --biome TemperateForest --count 8 --seconds 7200 --stability-days 3
```

This generates an ordinary LostTribe start, retains the preparation manifest and
unchanged save, then uses supervised acceleration, batched native observations and
placement previews. The seven-day food target and observed crop replenishment are
required alongside sustained gates. Vary the explicit seed/biome and extend the
stability window for seasonal trials. Each run has a fresh output and private Linux
state volume; a timeout, native hold or failed food assertion remains a failure.
Rough is the default difficulty. Peaceful runs must be labelled separately.
Use `--scenario Crashlanded` to select that native start instead of the default
`LostTribe`; `--disable-hunting` labels an explicit crop-focused work policy.
The world seed does not freeze native pawn generation: compare immutable prepared
saves for repeatability, and retain each fresh start's manifest and preparation report.

For bounded recovery acceptance through the same launcher, use
`python scripts/b04f_acceptance.py --root /worker/run --case drafted-medical --seconds 600`
or `--case refused-preview`. These require `EmergencyDevelopmentFixture=true` in
the private companion. The former checks actual tending and player draft ownership
after threats clear; the latter exercises real native order and stale need-admission
refusals and verifies subsequent controller reviews. They do not certify sustained food
or active-combat triage.

## Repeated model campaigns

```powershell
.venv\Scripts\python.exe scripts\headless_iterations.py --iterations 20 --parallel 2 --output .rimbot/campaign-new
.venv\Scripts\python.exe scripts\parallel_headless_smoke.py --output .rimbot/parallel-new
```

Use fresh output directories, prepared baseline/mods and local LM Studio. Each worker
owns its controller, SQLite state, save/config profile, log and GABS runtime. Installed
game/mod files are shared read-only. Start with two workers; eight is a configured
maximum, not a throughput recommendation. Commit between batches so workers import a
fixed revision. Use `--consecutive 3` for the baseline acceptance gate and `--direction`
to record the same player objective before each run. Use `--source-root` for a prepared
baseline elsewhere. Every dispatched trial receives an isolated profile. The runner
stops after the requested consecutive passing streak and retains already-running
siblings' evidence. A changed revision, model, or objective resets the streak;
interrupted trials cannot contribute. Workers also save `manifest.json` before startup.
Multiple consecutive passes require matching source-content, effective
inference-setting, baseline/profile, GABS, installed observation and colony identity
DLLs, and (for no-graphics launches) installed headless DLL fingerprints. Listed
untracked code is included alongside tracked source so new modules cannot silently
escape the source hash. Model weights and remaining mod binaries are outside that
fingerprint and must be held fixed by the operator.

Regenerate disposable profiles to remove legacy executable-name cleanup fallback.
Generated profiles use DirectPath process ownership; missing or other launch modes are
rejected for headless workers.

## Visible comparisons and evidence

For a visible model comparison, use `--rendered --fixed-window --seconds 300` with
`--parallel 1` and the same `--direction` for every model. Rendered workers use a
private normal profile without HeadlessRimPatch or batch/nographics flags. Load one LM
Studio model at a time; preserve load/context/offload settings and separate startup
failures from gameplay outcomes. Reserve GPU memory for rendering.

The short campaign window defaults to 120 seconds and extends after a first action to
allow another 120 seconds. `--fixed-window` disables that extension; holds and model
failures can still stop a trial early. Its narrow foothold check requires eight living
colonists, eight nearby completed beds/spots, a nearby nine-cell stockpile and allowed
starting pemmican. It does not certify shelter, sustained food or survival. Any
fixture-specific warning acknowledgment is test-only; other holds stop play.

Campaigns use the runtime's paused deliberation and bounded execution policy; the runner
does not force Superfast after a review. Preserve older speed settings in historical
reports rather than treating those runs as directly interchangeable.

Each worker freezes `thresholds.json` before startup and samples native building and
zone readbacks for completion. Reports separate retained intent, attempted slots,
accepted effects and current completed objects, including sleeping-place overshoot and
removal deltas. Truncated or unavailable readbacks cannot establish success. Diagnostic
telemetry separately counts retained repeated/rejected calls, model context budgets,
dispatch latency and interventions. First observed pawn progress requires changed native
position or carried item during a continuing work job. Functional reports distinguish
roofed sleeping geometry, observed bed use and stockpile filter/grid configuration from
unknown access and sustained food work. Observation timestamps are sampling upper
bounds, not exact completion times.

## Simulation benchmark

With controller/game closed, benchmark disposable simulation separately:

```powershell
.venv\Scripts\python.exe scripts\native_speed_benchmark.py --headless --seconds 3 --repeats 2
```

The benchmark reloads the baseline, uses native forced-speed support and restores Paused
with boost disabled. Boost is excluded from normal strategist gameplay. Report
interruptions, native tick rate and end-to-end throughput separately. Headless
simulation and parallel lifecycle isolation do not establish faster model inference.
Full episode comparisons must include model waits and useful outcomes.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
