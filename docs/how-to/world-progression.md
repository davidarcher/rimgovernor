# Verify world progression

[Documentation](../README.md)

`scripts/container_world_progression_acceptance.py` accepts the same `--game`,
`--mods`, `--profile` and `--gabs` Linux input directories as the lifecycle runner.
Use a task-specific `--image` and a new `--output` directory. It builds and pins
one worker image, retains the exact probe hash, native calls and failures, and
removes only its own container. `result.json` requires both native success and
cleanup. No model inference is exercised. Linux workers keep mutable GABS claims
and the game profile on their private Linux filesystem; after stopping the owned
game, the probe retains that complete runtime under `native-runtime`.

Add `--trip --shared` to require ordinary cargo packing, world movement and living
home-map return through semantic commands, shared reservations and Hands. Omit
`--shared` for raw native contract acceptance. Starting supplies receive ordinary
Unforbid orders and an ordinary stockpile; items outside home/storage are not
silently made eligible for native caravan loading. One identified Ancient danger
warning can be acknowledged after confirming no active hostile or hunting
predator. A single mad-animal notification can receive explicit scenario review
when every native hostile is beyond the configured proximity radius and no
predator is hunting colony members. Native proximity and injury stops remain
active. Settlement and prepared-colony cases can invoke the existing shared
ActiveCombat method after a hostile proximity stop. They require native threat
resolution and managed draft cleanup before continuing; new threats, injury
stops and unavailable defense methods still fail with retained evidence.

`--quests` requires the separately built interruption fixture and checks an
ordinary WandererJoin quest's terminal state plus the actual joined colonist.
It also generates an ordinary native TradeRequest offer, selects an observed
reward through AcceptQuest and verifies native acceptance. This does not certify
delivery of that trade quest's requested goods.
Hidden fixture quest evidence stays outside the gameplay observation surface.

`--logistics --trip --shared` additionally loads silver, issues an explicit hold,
and waits for returned silver to leave the pawn inventory and enter native storage.
`--multimap` extends that trip by settling a second native home and checking old-map
order refusal. Supply a private profile with the ordinary maximum-settlements
preference set to at least two. The fixture never edits maps or pawn positions.

`--diplomacy --trip --shared` chooses a settlement using native negotiation
eligibility and route/food limits, spends an explicit silver gift, observes
goodwill, then verifies return storage. `--quest-trade --trip --shared` requires
a native offer with obtainable goods and an item reward, uses shared acquisition,
and observes native fulfillment and received cargo. It can inspect visible ore
or wild herbs, move through shared pawn orders to an observed approach, and then
use the same native acquisition guards. Offers generated at later native ticks
retain their independently generated quantities; no requirements are edited. Unobtainable offers do not
authorize generated goods or changed quest requirements. These longer trips keep
home food gathering in the existing shared goal and Hands.

`--expired` waits for the ordinary short-lived ThreatReward_Raid_Joiner offer to
expire without acceptance, then verifies native acceptance refusal and terminal
outcome evaluation. No quest deadlines or ticks are edited.

`--days N` runs the deterministic controller with baseline living-roster and
cold-weather readiness samples every five seconds, retaining native ticks and
scope. A 30-minute wall bound fails stalled runs; safety stops remain active.
Warm-weather survival
does not establish winter acceptance. Cold exposure, stored food and usable
indoor sleeping must all be observed before the winter-readiness predicate passes.

`--matrix --trip --shared` additionally requires native cargo reserve and competing
manifest refusals before dispatch, an ordinary ColdSnap with freezing exposure
and a negative readiness result for the bare baseline, and an ordinary
AnimalInsanitySingle incident that blocks clock advancement. These fixtures call normal
incident workers; they never edit temperatures, pawn health or quest success.
The matrix verifies conservative evaluation and emergency stopping, not combat
victory or winter survival. Broader sustained foothold coverage remains in B04.


Use --prepared-days with --days to evaluate an existing normally played colony's
work and shared food gathering. This narrower mode does not certify autonomous
establishment. Settlement trips can prepare simple meals through native cooking
bills when the colony has a usable station and cook; production must be observed
before departure. The runner records the input save hash for either mode.

`--recovery --trip --shared` holds a normally formed party until native ration
consumption reaches the expedition recovery threshold, then requires an explicit
return with living crew. It never deletes food or edits pawn needs.
