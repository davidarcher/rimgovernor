# Encounter prediction

[Documentation](../../README.md) · [ColonyForecast](../architecture/colony-forecast.md) · [Combat research](../architecture/combat-predictability-research.md)

`IEncounterPredictor` is a prediction-only Domain boundary. `EncounterRequest`
states one observed scene, its world/tick/build/mod identity, actual body-part
health, equipment condition and quality, terrain/roof cells, wild herd, native
Hunt targets and timed participant departures. It contains independent experiment
seeds, never the live RNG state. No native facade, order action, process handle,
roster selection, demand calculation or journal belongs to this boundary.

`LocalEncounterPredictor` in Host adapts that contract to the existing isolated
real-engine host. It returns empirical per-seed duration, down/dead, withdrawal,
injuries/bleeding/tending inputs and corpse products. These are hypothetical
consequences, never observed colony inventory. Hauling, butchery, treatment,
recovery duration and displaced colony work remain ColonyForecast inputs counted
once downstream. Injury severity alone does not establish recovery labor.

## Lifecycle and bounds

One service instance owns one process, one active request and **no waiting queue**.
A concurrent request returns `Busy`; nothing is silently dropped, retried or
replaced. The caller supplies positive finite cold-start, request, cleanup,
sample, tick, map, actor and frame budgets. The hard ceilings are one minute for
each startup/request, five seconds for cleanup, 64 seeds, one day of simulated
ticks, a 128-cell map side, 32 actors and 1 MiB frames. A worker accepts at most
eight requests to bound retained native arena state; exhaustion is explicit and
does not skip earlier reconstruction failures. Each online seed executes once. Identical-seed `Verify` replay is an offline native qualification regression across fresh arenas and processes; it is not duplicated inside the latency-sensitive request. The initial measurement uses one seed. Additional samples require a decision-relevant reason within the declared budget; this service does not invent confidence from a fixed sample count.

The worker has no colony endpoint or save input. Only its owned process tree may
be killed. Timeout/cancellation kills that worker and waits within one cleanup
budget. Failed cleanup retains the owned handle, reports its PID and refuses
further requests. Disposal cancels active work and bounds the wait. A later
explicit request may start a new worker after successful cleanup; it is not an
automatic retry of the failed request. Game stderr is drained without accumulating
unbounded planner memory; stdout is bounded line-framed protocol data.

`EncounterPredictionConsumer` bounds an arbitrary implementation even when it
ignores cancellation. It rechecks the complete request fingerprint against the
current request and returned identity. Changed health, build/mods, observations,
tactics or a helper's departure invalidates the reply. It validates sample IDs,
participants, duration, finite consequences and departure/event bounds. A stale,
malformed, missing, unsupported or late reply cannot authorize hunting.

Source and hypothetical-worker mod fingerprints are explicitly pinned separately. The worker does not load the live Governor controller. Its content fingerprint participates in engine identity; a reviewed report must qualify the exact source-to-predictor pair, never infer equivalence from equal package names.

Game build identity hashes the actual loaded game assembly. Mod identity hashes
loaded mods' XML and assembly content with relative names and package IDs. Engine
identity hashes private runtime DLLs and the engine/host/adapter assemblies. Same package
IDs with changed definitions are not equivalent provenance.

## Coverage and qualification

The native schema-3 readback gate remains authoritative. A reconstruction mismatch
(including [#3159](https://github.com/davidarcher/rimgovernor/issues/3159)) is a failed
request, never corrected terrain or an alternative engine. The adapter requires
explicit positive equipment hit points/quality; native unknown definitions reject.
The supported tactic is native Hunt with timed leave/flee departures. The game reserves a hunted
animal for one pawn and a forced Hunt order steals that reservation, so two or more hunters naming
the same prey act as a drafted squad focus-firing it (`AttackStatic`), as a colony roster hunt does; a
lone hunter keeps the undrafted Hunt job. The hunt comparison uses a timed `Leave` for a gunner-retriever: the
gunner the roster sends off to haul at the median encounter tick its plain roster measured (#3513). Later arrivals, colony tactics, custom action sequences and unstated mechanics are
unsupported. The observed-environment input carries outdoor temperature and sky glow, held constant for the bounded encounter. Current schema does not represent changing weather/light or time of day,
food/rest/mood, bonds, mental breaks, arbitrary gene/hediff runtime state or dynamic
events. The observation producer must list such unrepresented effects in
`UnsupportedObservations`, which rejects before worker launch. No live observation
producer or hunting selector cutover is claimed here; #3130 owns integration.

A result's model uncertainty is separate from its empirical samples. A replay
check is not sampling calibration. `EncounterQualification` can reference a
reviewed report, criteria revision, supported domain, engine/build/mod/tactic
and covered request. The consumer attaches it only against a caller-supplied
reviewed report record with trusted build/mod/tactic/engine/domain/request scope;
the provider need not self-certify and cannot expand the scope by echoing new inputs. There are currently **no accepted reports**;
the real worker supplies no qualification. A fake qualified implementation can
exercise the independent consumer contract, but does not qualify the real engine.

The predeclared research gates remain: zero reconstruction defaults/mismatches;
15-second cold start, 1-second warm request, 2-second cancellation/cleanup;
per-cell severe-harm and prey-downed calibration within 0.2, native median duration within the predictor interquartile range widened by 2;
correct hunter-set ranking where observed harm rates separate; zero observed false-safe cases,
with a one-sided 95% upper frequency bound at most 1%. Missed serious injury/death
or contribution after departure fails the case. These are research proposals,
**not colony risk weights or a safety policy**. One seed cannot establish these
statistical gates. Held-out health, terrain, herd and departure evidence belongs
to [#2825](https://github.com/davidarcher/rimgovernor/issues/2825); broader runs require
nightly or explicit authorization.

## Evidence still required before selection

A direct engine run and its worker replay share the same simulation implementation.
Their agreement establishes service fidelity and repeatability; it cannot measure
prediction error against the live game. Repeating an identical seed also does not
add independent observations to a false-safe denominator.

The next qualification experiment belongs to #2825's existing CombatLab runner
and retained bundle/report lifecycle. Before a broader run, freeze the supported
scenario class, prediction interval construction, serious-harm/false-safe event
classification, held-out split and seed schedule. Retain rejected and unresolved
scenarios as well as successful predictions so abstention cannot masquerade as
accuracy. The worker exports samples; the [CombatLab qualification protocol](../testing/hunting-prediction-qualification.md) supplies independent native comparisons and hunter-set ranking, harm calibration and false-safe scoring (`hunt-native-v2`). Collection and acceptance remain outstanding.

The required evidence is:

- Matched initial native readback and independent observed outcomes, with build,
  mod, tactic, scenario and seed provenance. Prediction seeds must be independent
  of the live simulation's future randomness.
- Held-out health, distance, terrain, herd and departure variations. Observe
  actual prey retaliation and its injury/death consequences; inferred use of the
  game's Hunt job does not prove that behavior transferred correctly.
- Duration errors, declared interval coverage, separated-pair ranking, missed
  serious injury/death, contribution after departure, unresolved rate and tails,
  using the predeclared criteria above. Downstream recovery labor remains separate.
- Startup, first-request, warm-request and cancellation/cleanup measurements,
  including failures. A diagnostic timeout ceiling is not a passing latency gate.

The former `combatlab/hunt-squad` and `combatlab/hunt-door` execution cases were deleted with the acceptance
port (#3399): they used a quiet world with a substituted permanent manhunter, so they were never evidence for
revenge frequency. Qualification reuses the CombatLab machinery with revenge enabled and observed.

Even with zero failures, the exact one-sided 95% binomial upper bound is
`1 - 0.05^(1/n)`. At least 299 independent applicable safe predictions with zero
false-safe observations are needed to put that bound at or below 1%. This is a
necessary sample-size floor under the binomial assumptions, not a proposed run
budget or a sufficient qualification test: correlated repeats, unrepresentative
scenes and missing domain strata do not establish coverage. Any observed
false-safe still fails the separately declared zero-failure gate. A 20-seed pilot
can estimate noise but cannot satisfy this bound. Broad collection still requires
nightly or explicit authorization; no accepted report or #3130 cutover follows
from a lifecycle smoke alone.
## Reproducible lifecycle probe

The existing `RealCombat.NativeSmoke --predictor <absolute host DLL> --game
<private game> --fixture <absolute fixture directory> --out <absolute JSON>` mode
uses the committed hunting fixture's single seed. It compares direct native
outcomes with cold/warm worker responses, then checks unsupported input and owned
cancellation. Diagnostic ceilings of 60 seconds startup and 30 seconds request
measure a missed latency gate; they do not relax the 15-second/1-second research
gates. The retained JSON carries samples, provenance, phase timings and failures.
A direct/native-engine pair tests reconstruction and transport, not predictive
accuracy against an independently evolving live colony. Root xUnit tests use an
independent fake predictor for cancellation, changed availability, malformed
consequences, unsupported/unavailable replies and qualification scope.

#3129 remains the completion owner until the implementation is qualified and
useful at bounded runtime. #3130 remains blocked on that result, not on mere
engine startup or interface compilation.

The local engine owns one arena map and settlement per request and disposes them
before accepting the next request. Construction restarts at the same world tick;
explicit ground reconstruction clears generated temporary terrain layers. The
[real-engine lifetime regression](../../../integrations/rimgovernor-realcombat/README.md#arena-lifetime-and-repeatability)
requires four identical native hunting outcomes and terrain/readback results in
each process, plus byte-identical results across two processes. It covers the
reported repeated-arena defect, not qualification of prediction error or runtime.
The earlier 1.903-second first request missed the proposed one-second research
gate; this fix does not remeasure or relax that gate. #3129 remains open.
