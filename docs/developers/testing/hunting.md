# Verify hunting screening and dispatch

[Documentation](../../README.md)

Check native hunting observations and exact-target designation; actual hunting labor
needs a separate outcome assertion.

[Native test prerequisites](README.md#native-test-prerequisites).

`scripts/hunting_screen_probe.py --source-root <prepared-root> --output <new-directory>
--port 8788` starts a disposable visible colony and samples native wildlife for the
deterministic hunting screen. It retains observations, candidates and predator
rejections in `result.json`, with no hunting orders or model calls. This checks native
observation compatibility; boundary/unknown-data rejection and compiler integration are
tested separately. It does not prove reachability or successful hunting. Add
`--dispatch` to issue one designation through the deterministic compiler and shared
Hands under a scripted PLAYER goal in Manual. It verifies exact-prey native readback and
one write, then stops the disposable game. Rejection races are covered by runtime tests;
this fixture does not certify predator movement during live hunting.

For actual hunting and butchering, run `scripts/food_hunting_acceptance.py
--source-root <prepared-root> --output <new-directory>`. It assigns work, equips
bows, designates screened prey and creates an ordinary butcher spot and bill
through shared Hands. The exact prey corpse must be observed before its native
meat stock increases. Setup, route evidence, clock stops and pawn samples are
retained. Add `--checkpoint <native-save.rws>` for targeted acceptance from an
unmodified native save; this does not certify fresh-colony survival.
For Docker sources without Git metadata, add `--source-snapshot`.

With a private `FoodObservationFixture=true` native build, `--spoilage` forbids one
ordinarily butchered stack through the native designator and waits for actual rot.
The passive fixture retains the stack after destruction and tracks shared-stock
ingestion through native stack splits. Acceptance requires varying observed
temperatures and multiple pawns consuming a shared food origin. No food aging,
temperature edits or boosted time are used.
To isolate a temperature transition, use an unchanged native autosave with
`--checkpoint <save> --spoilage --observe-food-id <id> --vary-temperature`. This
queues ordinary campfire deconstruction and checks the retained stack's actual
temperature and eventual rot. It does not claim shared-ingestion acceptance;
that requires the complete hunting/spoilage run's separate native ingestion evidence.

With the same fixture, `--preservation` adds and prioritizes an ordinary native
long-lived food recipe after butchering. Acceptance requires native recipe products
from pawn work and an increase in accessible product stock. This checks explicit
preservation orders; automatic rot-risk selection has separate controller tests.
Use `--preservation-only --checkpoint <save>` to isolate the same recipe/output
check in an untouched save with existing cooking and work setup. Native bill
diagnostics retain ingredient shortfalls; normal bounded wild-plant harvesting
can supply ingredients. This mode does not claim hunting acceptance.

`--soil-crop` allows normal starting supplies, assigns work, and selects potatoes
for sixteen observed free cells at 70% fertility using the shared crop selector
and native food/climate facts. It requires actual sowing in that exact zone.
Use a normal prepared start with sufficient food runway and an open growing
season; a mismatched starting condition is a failure, not a simulated pass.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)
