# Verify native material extraction

[Documentation](../../README.md) · [Contracts](../contracts/mining-contracts.md)

Prepare [private Linux inputs](docker-inputs.md) and compile the observation companion
with `-p:MiningFixture=true`. Copy those DLLs into a private test mod snapshot before
launch. Never install this fixture in a normal game or replace running game DLLs.

Run a fresh [Docker worker](docker-worker.md) with this command:

```text
python scripts/mining_acceptance.py --source-root /worker/run --output /worker/probe --seconds 900
```

The fixture retains one capable baseline miner, holds the other pawns alive off-map,
permits nearby existing food, clears natural roof/rock and fog in a disposable test
area, places native ore at full hit points and schedules mining then hauling. It does not
grant mining skill, change extraction speed, finish pawn work or spawn recovered stock.
The scenario checks roof refusal, cancellation, a roof added after designation, shared
Hands dispatch, native recovered output, material hauling, depletion refusal and
paired restart with a pending dig. Inspect `result.json`
and `progress.json`, native logs and the worker's input hashes. Every assertion must
pass; setup errors and timeouts are failures, and failed output directories are retained.

Run deep development in a separate fresh worker/output directory:

```text
python scripts/deep_mining_acceptance.py --source-root /worker/run --output /worker/probe --seconds 900
```

Its disposable baseline has researched equipment, a fueled native generator and
scanner, ordinary construction supplies, an existing qualified builder and miner, and two known
deep deposits. Before the goal begins, the fixture sets the nearest selected seam to
one unit and the other to two native portions. These are scenario inputs. Construction,
drilling speed, mining yield, recovered stock and hauling are native pawn outcomes.
The scenario stages storage and two drills through shared Hands, restores drill
ownership through a paired restart, checks pawn-operated power release and depletion
replacement, verifies a satisfied stock target stops further work while ore remains,
and observes the output in storage. `result.json` retains native power, pawn and
extraction observations throughout. Initial grid settling is bounded; setup failures
cannot count as accepted facilities.

These scripted scenarios do not exercise local-model interpretation or general
tunnel support. Unsupported roofed excavation remains refused.
