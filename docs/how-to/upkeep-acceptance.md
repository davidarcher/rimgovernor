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

For new storage construction, use a fresh worker/output with
`python scripts/storeroom_acceptance.py --source-root /worker/run --output /worker/storeroom --seconds 240`.
The fixture prepares open ground, construction wood, exposed medicine and enabled
workers. The method must build the shell, observe native enclosure and roofing,
create the filtered zone, observe accepting capacity and verify delivered medicine.
The driver resumes bounded Hands passes after observation review; native dispatch
and context guards remain active.

For medical reserves, use
`python scripts/medicine_reserve_acceptance.py --source-root /worker/run --output /worker/medicine --seconds 240`.
The fixture removes existing medicine, prepares mature native wild medicine plants
and enables skilled plant workers. Shared resource actions must produce observed
medicine through ordinary harvesting and leave every patient's care setting intact.
This bounded case does not establish cultivated healroot production or recurring
seasonal supply.

For starting-animal containment, use
`python scripts/animal_containment_acceptance.py --source-root /worker/run --output /worker/animals --seconds 240`.
The fixture supplies open ground, wood, a loose muffalo and a pet. Shared work
allocation must enable ordinary handling, construction must complete a fence/gate
and marker, and native observations must place the muffalo inside the suitable pen.
The pet and release/slaughter settings remain unchanged. Simulation uses the
bounded native scenario supervisor; this case does not certify stored feed reserves.

The storeroom scenario also injects one native construction fumble and requires
continuous blueprint/frame/finished-building lineage for every room piece. It
joins those identities to confirmed autonomous receipts, checks alternate roof
support, and verifies that an identical independently replaced wall inherits no
ownership. A sole-holder fixture must fail the read-only removal-support check.
These checks do not perform a wall upgrade or certify enclosure during replacement.
Ordinary medicine hauling requires the native quantity ledger's protected-delivery
tick. A separate disposable-stack fixture exercises native split/merge operations,
partial delivery, destruction before delivery and retained proof after consumption.
Those accounting checks are distinct from the scenario's pawn-labor acceptance.
The scenario creates a paired checkpoint and restarts the private game. It requires
unchanged construction identities and plan receipts, retained delivery/loss proofs,
and exact resolution of both pieces of a pending split stack in Manual. The shared
hauling method then issues ordinary pawn work for surviving exposed portions.
Require the original saved quantity obligation to complete through native delivery;
the initial split stacks remain fixture inputs.

For stone production and a straight-wall upgrade, use
`python scripts/wall_upgrade_acceptance.py --source-root /worker/run --output /worker/stone --seconds 600`.
This extends the storeroom scenario with empty exterior backup cells, chunks,
native raw workbench materials and enabled workers. Missing research must first
refuse construction; the fixture then supplies that prerequisite for the labor
case. No blocks, workbench or bill are supplied. Require ordinary
stonecutting, construction of the backup enclosure, native demolition of one owned
wall, permanent stone construction and backup removal. Observe roof/enclosure
between stages. A second batch is interrupted through Manual after its demolition
receipt; require the original wall to survive further simulation and paired restart
without replay. This does not establish recovery of the interrupted batch.

For a corner with existing support and open salvage access, use
`python scripts/corner_upgrade_acceptance.py --source-root /worker/run --output /worker/corner --seconds 900`.
Require funded stone replacement, ordinary demolition and completed construction
with retained roof/enclosure. No temporary walls may obstruct exterior salvage
access. Both probes preserve unknown roof-support geometry as a blocker.

For animal feed, use
`python scripts/animal_feed_acceptance.py --source-root /worker/run --output /worker/feed --seconds 300`.
The fixture supplies a hungry pet, a butcher spot, ingredients outside the pet's
allowed area and enabled cooks. It supplies no kibble or bill. Require ordinary
production, recovery of the reachable reserve and an observed increase in the
animal's food need through eating. The reserve includes native competing eaters;
the test preserves animal removal settings. This case does not establish protected
delivery inside pens, ingredient replenishment or sustained seasonal feeding.

For dining and recreation, use
`python scripts/comfort_acceptance.py --source-root /worker/run --output /worker/comfort --seconds 600`.
The disposable fixture supplies a warm roofed room, wood and enabled builders.
The shared method must construct a table, an adjacent dining chair and a recreation
object. Declared hunger and recreation deficits then exercise ordinary native use;
construction receipts alone cannot clear the maintained goal. The test compares
the saved timetables before and after use. It does not certify every colonist's
future recreation choices or sustained satisfaction under changing conditions.

Require process exit zero and `result.json.outcome == "passed"`. The report retains
the source/input manifest, installed order schema, fixture setup, observations,
receipts, postconditions and plan. Keep container and native logs alongside it.
Failed trials must remain available; use another output directory after a fix.

This probe establishes only its declared targets and bounded outcomes. It does not
certify arbitrary bed replacements, safe wall replacement, sustained
food/feed/medical production, seasonal preparation or the complete B04h campaign
matrix. Those acceptance requirements remain in [the backlog](../BACKLOG.md).
