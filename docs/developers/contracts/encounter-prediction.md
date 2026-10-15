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
does not skip earlier reconstruction failures. Each seed is run twice
(`Verify`) to detect replay disagreement: a sample budget of N means up to 2N
engine fights, not N. The initial measurement uses one seed. Additional samples
need a decision-relevant request reason and remain within the supplied cap;
this service does not invent confidence from a fixed sample count.

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

Game build identity hashes the actual loaded game assembly. Mod identity hashes
loaded mods' XML and assembly content with relative names and package IDs. Engine
identity hashes private runtime DLLs and the engine/host/adapter assemblies. Same package
IDs with changed definitions are not equivalent provenance.

## Coverage and qualification

The native schema-3 readback gate remains authoritative. A reconstruction mismatch
(including [#3159](https://github.com/davidarcher/rimgovernor/issues/3159)) is a failed
request, never corrected terrain or an alternative engine. The adapter requires
explicit positive equipment hit points/quality; native unknown definitions reject.
The supported tactic is native Hunt with timed leave/flee departures; later
arrivals, colony tactics, custom action sequences and unstated mechanics are
unsupported. Current schema does not represent time of day, temperature,
food/rest/mood, bonds, mental breaks, arbitrary gene/hediff runtime state or dynamic
events. The observation producer must list such unrepresented effects in
`UnsupportedObservations`, which rejects before worker launch. No live observation
producer or hunting selector cutover is claimed here; #3130 owns integration.

A result's model uncertainty is separate from its empirical samples. A replay
check is not sampling calibration. `EncounterQualification` can reference a
reviewed report, criteria revision, supported domain, engine/build/mod/tactic
and covered request. The consumer retains it only against a caller-supplied
reviewed report record with trusted build/mod/tactic/engine/domain/request scope;
the provider cannot expand the scope by echoing new inputs. There are currently **no accepted reports**;
the real worker supplies no qualification. A fake qualified implementation can
exercise the independent consumer contract, but does not qualify the real engine.

The predeclared research gates remain: zero reconstruction defaults/mismatches;
15-second cold start, 1-second warm request, 2-second cancellation/cleanup;
median duration error at most 20%; at least 90% coverage for declared 90% intervals;
correct ranking where observed intervals separate; zero observed false-safe cases,
with a one-sided 95% upper frequency bound at most 1%. Missed serious injury/death
or contribution after departure fails the case. These are research proposals,
**not colony risk weights or a safety policy**. One seed cannot establish these
statistical gates. Held-out health, terrain, herd and departure evidence belongs
to [#2825](https://github.com/davidarcher/rimgovernor/issues/2825); broader runs require
nightly or explicit authorization.

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

Using the same committed hunting fixture and seed, cold startup was 8.856s,
first request 1.903s, warm request 0.535s and active cancellation 275.7ms. The first
run killed the boar; the warm run left it alive and untouched, despite both
internal replay checks passing. Hunting prediction remains unqualified pending
investigation of repeated-arena behavior. This does not establish the same cause
as the terrain drift already tracked in #3159.
