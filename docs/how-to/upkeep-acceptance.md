# Verify native upkeep

[Documentation](../README.md) · [Upkeep contracts](../reference/upkeep-contracts.md)

Use a disposable Linux colony and private task DLLs. Follow [Docker input
preparation](docker-inputs.md), adding `-p:UpkeepFixture=true` when building the
observation assembly. This includes a test-only setup tool; ordinary production
builds omit it. Copy the resulting DLL into private mod inputs before launching
the worker. Never update a running worker's inputs or a shared game installation.

Run a fresh [Docker worker](docker-worker.md) with this command:

```text
python scripts/upkeep_acceptance.py --source-root /worker/run --output /worker/upkeep --seconds 120
```

The worker image must contain the matching controller source. The output must be
new. The probe prepares separate targets for hauling, repair, cleaning and a small
fire, compiles deterministic actions into ColonyPlan, executes them through Hands,
and observes the native postconditions. The hauling case starts with empty covered
space: the deterministic method creates a filtered stockpile through Hands, then
orders hauling and verifies the delivered medicine. A second guarded creation
attempt on the occupied zone must refuse. Other fixtures supply a damaged wall,
aged dirt and enabled workers. Setup does not establish building construction
acceptance; subsequent work uses normal game jobs without inference.

The animal observation case distinguishes a penned animal, a loose pen animal and
a pet, and checks reachable feed inside a closed enclosure. An allowed-area fixture
then requires native hauling, repair and cleaning writes to refuse excluded targets.
These assertions do not establish autonomous pen construction or feed replenishment.

For sleeping upgrades, use a fresh worker/output with
`python scripts/sleeping_upkeep_acceptance.py --source-root /worker/run --output /worker/sleeping --seconds 240`.
The same fixture build prepares a warm room, wood and completed native bed research.
Hands creates the starting
floor spot and retains its native identity. The method must construct a bed through
ordinary pawn work, preserve the floor, transfer the unchanged assignment, refuse
a stale assignment request, and observe the pawn using the new bed. Low rest is
a declared test input; normal native behavior performs the subsequent sleeping.

Require process exit zero and `result.json.outcome == "passed"`. The report retains
the source/input manifest, installed order schema, fixture setup, observations,
receipts, postconditions and plan. Keep container and native logs alongside it.
Failed trials must remain available; use another output directory after a fix.

This probe establishes only its declared targets and bounded outcomes. It does not
certify new storeroom construction, arbitrary bed replacements, safe wall replacement, sustained
food/feed/medical production, seasonal preparation or the complete B04h campaign
matrix. Those acceptance requirements remain in [the backlog](../BACKLOG.md).
