# Verify world progression

`scripts/container_world_progression_acceptance.py` accepts the same `--game`,
`--mods`, `--profile` and `--gabs` Linux input directories as the lifecycle runner.
Use a task-specific `--image` and a new `--output` directory. It builds and pins
one worker image, retains the exact probe hash, native calls and failures, and
removes only its own container. `result.json` requires both native success and
cleanup. No model inference is exercised.

Add `--trip --shared` to require ordinary cargo packing, world movement and living
home-map return through semantic commands, shared reservations and Hands. Omit
`--shared` for raw native contract acceptance. Starting supplies receive ordinary
Unforbid orders and an ordinary stockpile; items outside home/storage are not
silently made eligible for native caravan loading. One identified Ancient danger
warning can be acknowledged after confirming no active hostile or hunting
predator. Other interruptions fail with retained native evidence.

`--quests` requires the separately built interruption fixture and checks an
ordinary WandererJoin quest's terminal state plus the actual joined colonist.
It also generates an ordinary native TradeRequest offer, selects an observed
reward through AcceptQuest and verifies native acceptance. This does not certify
delivery of that trade quest's requested goods.
Hidden fixture quest evidence stays outside the gameplay observation surface.
`--days N` adds ordinary simulation with baseline living-roster checks at most
6,000 ticks apart and cold-weather readiness samples. Warm-weather survival
does not establish winter acceptance. Cold exposure, stored food and usable
indoor sleeping must all be observed before the winter-readiness predicate passes.

`--matrix --trip --shared` additionally requires native cargo reserve and competing
manifest refusals before dispatch, an ordinary ColdSnap with freezing exposure
and a negative readiness result for the bare baseline, and an ordinary MadAnimal
incident that blocks clock advancement. These incident fixtures call normal
incident workers; they never edit temperatures, pawn health or quest success.
The matrix verifies conservative evaluation and emergency stopping, not combat
victory or winter survival. Broader sustained foothold coverage remains in B04.

