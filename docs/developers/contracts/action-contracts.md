# Action completion contracts

[Documentation](../../README.md)

An accepted native order and a completed action are separate states. The table defines
what each action must observe.

| Committed action | Completion boundary |
| --- | --- |
| `build_room_shell`, `place_buildings` | Native building observations through ProjectBook; a shell does not certify roofing or usable shelter. |
| `create_zone` | Validated native zone geometry/readback; storage and crop production are separate outcomes. |
| `native_operation` | Schema-validated native receipt/readback by default. Bills, settings, designators and UI actions do not imply downstream pawn labor finished. |
| Existing-zone edits | Fresh native geometry, crop or filter must match the requested native edit; deleted zones must be absent. |
| Bill ingredient whitelists | Exact native bill identity and filter must match fresh readback; production remains separate. |
| Medical native operations | Optional `patient_tended` and `patient_in_bed` wait for fresh living-patient observations. These certify current treatment/delivery state, not full healing or actor attribution. |
| Waste native operations | Required `waste_contained` verifies the exact item in separated storage or a grave on a later native tick. Relocation does not mean destruction; explicit burial requires the body inside a grave. |
| Population native operations | Orders/settings have receipt boundaries; [population goals](population-contracts.md) separately observe custody, care, recruitment and work/equipment/housing integration. Current-load observed custody/settings can resolve an uncertain order without replaying it. |

| Upkeep native operations | `upkeep_target` waits for protected stock, repaired target health, or cleaned target absence. Ownership, quantity and unknown-state rules are in [upkeep contracts](upkeep-contracts.md). |
| `trade` | Guarded open/stage/preview/accept with participant, content and silver-budget checks; hauling/storage remain separate. |
| Caravan formation | Exact living crew with an observed loaded departure; an assembly receipt alone does not complete formation. |
| Quest acceptance | Fresh scoped acceptance tick; native quest success remains a separate observed state. |
| `movement` (Actions/Apply `MoveIntent`) | Walk-to-cell order for a pawn its plan drafted, an intent-mode kind: native applies it when the pawn is alive, spawned, drafted and the cell is standable and reachable, and applies an order that already matches (the pawn stands there or is walking there) as a no-op. The applied receipt is terminal and says nothing about arrival. A shrine plan's breach or opening depends on its moves, and the worker holds that final action until every mover stands on its cell, re-sending the move of a drafted pawn idle elsewhere. |
| `clock` | Native clock control; it certifies no combat victory. |
| `draft` (Actions/Apply `DraftIntent`, intent-mode) | Sets or clears one colonist's draft under Auto. Drafts are plan-owned (#939): there is no native claim, and the undraft sweep undrafts every drafted colonist no live plan needs. |
| `building_temperature` (`BuildingPatchIntent` target_temperature, intent-mode) | Setpoint on one exact `CompTempControl` building, in the game's -273.15..1000 C interface range. Applied means set; the next building read confirms it. No snapshot token. |
| `bed_medical` (`BuildingPatchIntent` medical / for_prisoners, intent-mode) | Medical flag or prisoner use on one exact humanlike `Building_Bed`; the game's setter drops every owner. A bed def that cannot be medical, or a crib / non-prison room for prisoners, is refused. No snapshot token. |
| `grower_crop` (`BuildingPatchIntent` plant_def, intent-mode) | Crop on one exact `Building_PlantGrower` under the game's set-plant gizmo rules (sowable, the grower's sow tag, sow research finished). No snapshot token. |
| `claim_building` (`BuildingPatchIntent` claim, intent-mode) | Claim of one exact claimable building for the player (#459): `Building.ClaimableBy(player)` then `SetFaction(player)`; a building already the player's applies again. Nothing opens a casket. |
| `open_casket` (PAWN_ORDER_KIND_OPEN_CASKET) | An Actions/Apply `PawnOrderIntent` (#939): ordered vanilla `Open` job by one eligible colonist, drafted or not, on one exact filled `Building_AncientCryptosleepCasket`, validated live at apply (#460); native adds the `Open` designation under authority right before the job and removes it through the job's finish action if the job ends with the casket still full. Completion is the casket observed empty after the job; opening one casket ejects every casket of its shrine group, so a plan carries one order. Refused when the casket is empty, reserved, unreachable at its interaction cell or the pawn cannot do dumb labor. Behind the opening gate (#875). |
| `move_building` (`RelocateIntent`, intent-mode) | Re-sites one exact installed, minifiable player building through the game's Reinstall (#808): `GenConstruct.PlaceBlueprintForReinstall` at the destination cell and rotation, without the gizmo's `WipeExistingThings`. Ordinary construction work uninstalls and carries the piece, and needs `CanReserve` on it, so a pawn sleeping in a bed or working a bench is never interrupted. Identity, quality and hit points survive. A packed (minified) item, named by its own id or its inner building's (#830; `observations_list_supplies` carries `inner_id`/`inner_def_name`), gets `GenConstruct.PlaceBlueprintForInstall` instead. Applied (`QUEUED` with the blueprint id) means ordered; a blueprint already standing for the same placement applies again, and the next building read decides progress. |
| `uninstall_building` (`RelocateIntent` with `uninstall`) | Packs one exact installed, minifiable player building (#843) through the vanilla Uninstall designation (an existing one applies again). Ordinary construction work minifies it once `CanReserve` holds; vanilla hauling takes the packed item to storage (native never hauls). Applied (`UNINSTALL_QUEUED`) means ordered. |
| `bed_assign` (BedAssignIntent) | Ownership transfer of one exact vacant humanlike `Building_Bed` to one exact colonist on Actions/Apply, carrying the pawn's expected previous bed (or none); native checks at apply that the pawn is free (not dead, downed, drafted or in a mental state), that the expected previous bed still holds, and the bed's roof, forbidden state, allowed area, reach and the pawn's comfortable temperature band before `TryAssignPawn`; a pawn already owning the bed applies again. The applied result carries the ownership readback, but `MaintainHousing` recovers only on the pawn's observed sleep in that bed. |
| `Arrest` | Vanilla custody through `JobDefOf.Arrest`: an armed, violence-capable drafted arrester (plan-owned draft, #939), an exact target still in a mental state or a standing neutral `Faction.OfAncients` humanlike, and an exact usable prisoner bed. Native eligibility, manipulation, reservation and reach are rechecked; hostile aggressive states such as Berserk are refused. Optional pawn/target/bed tokens fence stale snapshots. Completion requires a later observation of the living target as a colony prisoner in that exact bed with its mental state ended, under the original order. Arrest resistance or interrupted delivery is unsuccessful; admission, mental-state recovery alone and custody before bed delivery do not complete it. `mood/arrest` proves refusals, replay and a legal sad-wander arrest. |

## Apply-time preconditions and refusal reasons

A routine write carries the snapshot token the controller read, but the
token is not the check. Every kind below re-evaluates its preconditions
inside the operation, on the main thread, at the tick the write applies
(nothing ticks between the check and the effect), in the order listed; the
first rule that fails is the refusal, so a world that moved under the order
names the fact that moved. The token comparison is always the last rule: it
still catches a change no rule names. The acquisition kinds (plant, mine,
hunt) accept a request without a token, and the controller's execute omits
it (#243): the worker dispatches them under a running clock, where the token
(growth, hit points, position) moves every tick, and the rules alone refuse
a moved world; a preview still sends it. A refusal is a terminal failure reply
(`InvalidRequest`; `NotFound` where the exact target is gone), never a
receipt: the worker reconciles it as `ReceiptRefused`, flight.jsonl keeps the
detail verbatim, and no Go code parses it. The detail is
`<Kind> refused: <reason>`; the `apply/refusal` case executes one write per
kind after moving the world under a token that was valid when read.

An acquisition inspection outrun by the live clock records a stale_facts hold
and invalidates its cached reads. The worker gives it one immediate fresh retry
before returning to ordinary backoff. A stale inspection never dispatches a
write; native ineligibility remains an ordinary hold.

| Kind | Preconditions, in order (the reason text is the rule that failed) |
| --- | --- |
| Zone creation (`CreateZone` intent; the zone already standing on the cells applies again, with its id) | Growing zones, per cell: `cell (x, z) is out of bounds or fogged`, `is not walkable`, `is outside the crop's growing season`, `holds a building, blueprint or frame`, `is already zoned`, `is marked for roof collapse`, `is not fertile enough for the crop`, `is refused by the native growing-zone designator`. Stockpiles, per cell: `fresh free ground required: cell (x, z) is not roofed, walkable, unzoned, empty storage ground`; a filter admitting only things with no deterioration rate (a chunk dump) drops the roof rule and reports `is not walkable, unzoned, empty storage ground`. Then, only when the intent carries the planner's census token, `the map's zone census changed since it was read`. |
| Zone cell edit (`ZoneCellsIntent`; cells already in (add) or out of (remove) the zone apply again) | `the exact zone no longer exists on this map` (NotFound); `the zone holds a phantom cell the zone grid does not map to it`; `the stockpile's haul grid no longer matches its cells`. Adding, per cell: `cell (x, z) is not free zoneable ground`, `is not fishable water in the zone's water body`, `already belongs to a storage group`. Removing, per cell: `cell (x, z) is not mapped to the zone on the zone grid`, `is not mapped to the stockpile's storage group`. Then `the edited zone would not be contiguous`. |
| Zone deletion (`DeleteZoneIntent`; a zone already gone applies again) | The phantom-cell and haul-grid rules above. |
| Stockpile settings (`StockpileIntent`; settings that already hold apply again) | The target is a stockpile zone or a player storage building (`Building_Storage`, e.g. a shelf). `the exact stockpile zone or storage building no longer exists on this map` (NotFound); `the target has no storage settings`; `the settings body does not resolve against the target's storable definitions`. |
| Allow / Forbid (`DesignateIntent`, intent-mode) | `the exact item is no longer spawned on this map` (NotFound); `the item is not a forbiddable haulable item`; `the item's cell is fogged`; `the item belongs to another faction`; `the native forbid designator refuses the item`. An item already in the wanted state applies again. Applied is terminal, refused replans. |
| Cut blighted plant (`DesignateIntent` cut_plant, intent-mode) | `the exact plant is no longer spawned on this map` (NotFound); `the plant is not blighted`; `the plant's cell is fogged`; `the plant stands outside the colony's growing zones and home area`; `the plant is forbidden`; `the native cut designator refuses the plant`; `no free colonist with plant cutting enabled can reach the plant`. A plant already designated applies again. Applied is terminal, refused replans; the goal settles on the census. |
| Deconstruct building (`DeconstructIntent`, intent-mode) | `Exact deconstruction target is absent.` (NotFound); the player-deconstructible, visible-geometry, not forbidden or burning, enclosing-colony-wall and roof-support safety refusals; `A pending wall upgrade owns the target.`; `Native deconstruction designator refused the target.` A player designation is adopted; a target the controller owns applies again. Applied means designated; revoking authority releases owned designations. |
| Haul (`HaulIntent` on Actions/Apply) | `the pawn is dead or downed`; `the pawn is in a mental state`; `the pawn is not a player-controlled colonist`; `the pawn is drafted`; `the item is gone` ; `the target is not a haulable item`; `the item is not spawned`; `the target's cell is fogged`; `no hauling job is available`. The refusal is the action result. The intent is idempotent: a pawn already hauling or carrying the item applies without a new order. Applied means ordered, not delivered (#856). |
| Work settings (`WorkSettingsIntent`; settings that already hold apply again) | `the exact pawn is no longer spawned on this map` (NotFound); `the pawn is not a living free colonist`; drug policy: `the pawn has no drug policy`; otherwise `the pawn is downed` (care-only writes exempt); `the pawn is drafted`; `the pawn is in a mental state`; `the pawn has no work settings`; `the game's manual-priorities setting is unreadable`; `a requested work type is not defined`; `a requested work type is disabled for the pawn`; `manual priorities are off, so only 0 or 3 can be set`; `the requested allowed area is missing, unreachable or unsafe under the current roof hazard`; `a requested timetable assignment is not defined`; `the pawn has no 24-hour timetable`; `the pawn has no medical care settings`; `the requested food is not natively eligible`. |
| Plant acquisition (cut/harvest, `AcquireResource` on a plant) | `a roof collapse is pending on this map`; `the exact plant is no longer spawned on this map` (NotFound); `the plant is not at the expected cell`; `the plant no longer yields the expected resource`; `the plant's cell is fogged`; `the plant is forbidden`; `the plant is not harvestable now`; `the plant stands in a growing zone`; `the plant is already designated`; `the native designator refuses the plant`; `no free colonist with plant cutting enabled can reach the plant`; `the plant snapshot changed since it was read`. |
| Cancel plant acquisition (`CancelAcquisition`) | `Cancellation requires an exact plant acquisition.` (InvalidRequest); `No live acquisition record for the plant.` (NotFound: the harvest finished, native restarted, or the resource/cell differ); the authority refusal. Applied evidence is the acquisition effect with `designated=false`; the withdrawn record then observes unsuccessful (`Harvest designation is gone and the plant remains.` / `Source is gone without an observed harvest.`). While a plant acquisition stays pending, its effect carries `pending_reason` (forbidden, fogged, burning, below harvest growth, no enabled plant cutter, none can reach, reserved, cutters busy on named jobs); the routine acquisition and medical planners cancel a designation still pending `AcquisitionStallTicks` (60000, the `RoutinePolicy.AcquisitionProgress` contract deadline) after its dispatch, the executor withdraws it so the goal re-plans (#291), and the source is keyed out for a bounded cooldown on the goal's progress record (#629). |
| Cancel hunt acquisition (`CancelAcquisition`) | Requires the preceding attempt of the same controller action to hold the original native hunt record, with the original source, corpse resource and admission cell on the same map. A fresh authority grant permits withdrawal after a safety stop; the animal may have moved. Withdrawal removes the hunt designation and interrupts active Hunt jobs targeting that animal. Native observation of the withdrawn record, with no designation and no observed kill, settles the acquisition as unsuccessful so the pest planner can admit a new method. |
| Mine (`AcquireResource` on a rock) | `a roof collapse is pending on this map`; `the exact rock is no longer spawned on this map` (NotFound); `the rock is not at the expected cell`; `the rock no longer yields the expected resource`; `the rock's cell is fogged`; `the rock is forbidden`; `excavation geometry is unsafe: <blocker>`; `the rock is already designated for mining`; `the native mine designator refuses the rock`; `no free colonist able to mine can reach the rock`; `the rock snapshot changed since it was read`. |
| Hunt (`AcquireResource` on an animal) | `two hunts are already outstanding on this map`; `a roof collapse is pending on this map`; `the exact animal is no longer spawned on this map` (NotFound); `the animal is dead`; `the animal is neither safe wild prey nor a recognised pest`; `the animal's corpse is not the expected resource`; `the animal's cell is fogged`; `the animal is already designated for hunting`; `the native hunt designator refuses the animal`; `no usable butcher bill with an assigned cook accepts the corpse`; `no free colonist with hunting enabled and an ordinary ranged weapon (or a melee weapon or bare hands against meleeable prey) has a safe route to the animal`; `the animal snapshot changed since it was read`. |
| Acquire (`AcquireIntent`, intent-mode, #1046) | Acquisition, mine acquisition and `acquisition_withdraw` dispatch one `AcquireIntent` on Actions/Apply. Designation runs the plant, hunt or mine rules above without a snapshot token; a source already designated applies again. With `withdraw`, the plant harvest/cut, hunt or mine designation is removed (a hunter already on the prey is stopped); a source already undesignated or gone applies again. Applied means ordered; the census reads progress. The acquisition and medical planners withdraw any designated census row of the goal's kind left untaken past its stall deadline, the player's own included, each as its own one-action method admitted before re-selection. |
| Husbandry (`HusbandryIntent`; an order that already holds applies again) | `order requires an animal and the order's argument`; `animal <id> is not a wild animal on the map` (tame) / `is not a player animal on the map` (NotFound); train: `animal cannot train <def>`, `animal is designated for removal`; slaughter/release: `animal is protected or native slaughter (release) eligibility refused it`; tame: `native tame eligibility refused the animal or it is designated for hunting`; area: `animal cannot carry an allowed area`, `allowed area <id> is not on the animal's map` (NotFound), `allowed area must preserve hazard protection and native reachability`; master: `a master requires learned Obedience`, `master <id> is not a spawned free colonist on the animal's map` (NotFound); following: `following requires learned Obedience`. |
| Production bill (`ProductionBillIntent`; a bench already carrying a matching bill applies again) | `bench <id> is not a loaded bill giver` (NotFound); `production tracking is unavailable`; `bench is not usable for bills`; `replacement must be the same recipe on this bench or an ordinary meal tier on this map`; `bill stack is full`; `bench already carries a matching <recipe> bill`; `recipe <recipe> is not available on the bench`; `Beer reserve requires a wort recipe`; `recipe <recipe> is not ordinary single-product work`; `replacement requires an ordinary meal recipe`; `ingredient filter does not fund the recipe's ingredient slots`; `recipe <recipe> has no work type on <bench>`; `no free colonist works <work type> within reach of the bench with the recipe's skills (...)`. Applied means the bill stands; its production is not observed. |
| Build (`PlaceBuilding`) | The placement is re-planned at execute with the same diagnostic the preview reports (`NativeConstructionPlan.Prepare`): footprint, terrain, stuff, reach and blocking-thing rules refuse with the preview's own reason text; an admitted plan then goes through the construction ledger. |
| Grower crop (`BuildingPatchIntent` plant_def) | `Exact plant grower is unavailable.` (NotFound); `Plant definition cannot be sown on this grower: ...`. No snapshot token. |
| Move (`RelocateIntent`, intent-mode) | `the exact building is not installed on this map` (NotFound); `not the player's`, `cannot be uninstalled`, `not rotatable; only north is valid`, `already stands at the destination`, `already has a reinstall blueprint elsewhere`, `designated for uninstall or deconstruction`, `destination is out of bounds or fogged`, `the game refuses a reinstall blueprint at the destination` (`GenConstruct.CanPlaceBlueprintAt`), `no free colonist with construction enabled can reach the building`. Packed: `the exact packed building is not on this map` (NotFound), `the packed building is not the player's`, `fogged or forbidden`, `already has an install blueprint elsewhere`, `the game refuses an install blueprint at the destination`, `no free colonist ... can reach the packed building`. No snapshot token. |
| Uninstall (`Uninstall`) | `the exact building is not installed on this map` (NotFound); `not the player's`, `cannot be uninstalled`, `has a reinstall blueprint`, `designated for deconstruction`, `no free colonist with construction enabled can reach the building`. No snapshot token. |
| Claim (`BuildingPatchIntent` claim) | `Exact claimable building is unavailable.` (NotFound); `No player faction to claim for.`; `The game refuses the claim (contents, faction or a spawn lock).` Already the player's applies again. No snapshot token. |
| Remove wall (`RemoveWallIntent`, intent-mode) | `Loaded map required.`; `Wall cell is outside the map.`; `A different wall stands at the cell.` (StaleIdentity, when `expected_wall_id` names another wall); the site blocker (enclosure, roof support, an eligible worker) or `No wall-upgrade site`; `Several admissible wall-upgrade sites share this wall`. A cell with no colonist wall, or a wall this controller already designated, applies again. No snapshot token. |
| Waste haul (`WasteIntent`, intent-mode) | `Exact colonist is not spawned on this map.` / `Exact waste item is unavailable.` (NotFound); `Waste haul requires an eligible undrafted pawn.`; `Item is protected or not eligible waste: ...`; `Already relocated; no further haul needed.`; `No native hauling job with an eligible separated storage or burial destination is available.` A pawn already hauling the item applies again. No snapshot token. |
| Service order (`RecoverIntent`, intent-mode) | `Exact pawn is not spawned on this map.` / `Exact serviceable building is unavailable.` (NotFound); `Service order requires an eligible undrafted pawn.`; `Observed service no longer needs this method.`; `No native service job is available for this pawn and target.` A pawn already servicing the building applies again. No snapshot token. |
| Excavate (`ExcavateIntent`, intent-mode) | `Current map required.`; `Roof collapse is pending on this map.`; `Expected rock is not visible at the cell.`; the cell blocker or roof-support blocker; `Native mining designation is not accepted at this cell`. A cleared walkable cell applies as cleared; a standing Mine designation is adopted. No snapshot token. |
| Bed assignment (`BedAssignIntent`) | `Current map required.`; `Pawn unavailable for bed assignment.` / `Bed unavailable: ...` (NotFound; the pawn's current job, whoever ordered it, is no refusal, #461); `Previous bed assignment changed; observe before recovery.`; `Native bed assignment eligibility refused.` |
| Home extension (`HomeIntent`, intent-mode) | `bounded visible native facility geometry is unavailable` (NotFound; the exact building/stockpile is gone, forbidden, burning or fogged). Native recomputes the batch (at most 256 cells from the building and connected enclosed roofed rooms) at apply and sets its missing Home cells; a batch already all Home applies again. Applied is terminal, refused replans; `changed_cells`, `covered` and the post-write revision are the evidence. |
| Research selection (`ResearchIntent`, intent-mode) | Known project, not anomaly knowledge, unfinished and startable now (`CanStartNow`); the project already current applies again. Applied is terminal, refused replans. |

Roof areas have no native write kind; combat/draft and medical writes keep
their own protocols.

## Trades

Map trades dispatch under a running clock like every routine kind (#244);
accept revalidates each staged thing at apply time. Opening requires a reachable, eligible
negotiator: an adjacent one opens the session at once; otherwise native orders
vanilla's TradeWithPawn job, which follows the trader, and opens the session
(never the dialog) where that job would open `Dialog_Trade`. An interrupted
walk is not reissued. Sheet reads, line staging, accept and cancel need only the session's
participants present and tradeable. Native sessions bind the exact deal,
participants, map and colony load.
Every trade operation is an idempotent intent naming the session's trader and
negotiator; accept also carries the preview signature covering exact rows,
counts, stock identities and prices. Native acceptance rechecks stock
eligibility, both silver balances and trader availability after any viewing
delay. Trade is an intent-mode kind (`domain.ActionKind.IntentMode`): an
applied receipt completes the action with no observation phase, a refused
one fails it, and a lost receipt sends the intent again. Dispatch does not
gate on a preview: native judges the intent against live state when it
applies. The routine replans from live state: the trade-session read
(`ReadTradeSession`) names the negotiator walking to or trading with a
trader, and reads issue no orders. Bought map goods appear at the carrying trader/pack
animal's native delivery location. The caravan lord prevents its own pawns from
retrieving them; this does not forbid the colony from using them. Exchange
completion does not certify storage.

Policy trades select bounded purchases and surplus sales from the fresh native
sheet. `economicFloors` on native acceptance contains exact `Def=count` entries
separated by semicolons, including Silver. Acceptance checks remaining actual
stack counts in the same main-thread operation as the exchange and refuses
protected exports. Unknown or truncated inventory prevents selection. A policy
with no eligible affordable lines cancels its own session without an exchange;
the retained policy evidence explains each target. Neither cancellation nor
acceptance takes a trader quest. Trade is routine-only (the `trade` family,
`--routine-silver-reserve`,
`--routine-item-wealth-share`); there is no player trade command. Sales come
from stock above a MaintainResource target and, once the item share of colony
wealth passes `--routine-item-wealth-share`, from raw-material hoards (steel,
plasteel, gold, uranium, jade) sold down to the highest of the target, the
economic floor and a retained minimum (`policy.WealthSurplus`); an unknown
wealth split sells nothing on that rule. A caravan reported still travelling holds the goal open and
lends native ticks until it arrives.

Direct orbital opening is refused. Ordinary orbital input requires the comms
console's native menu, a powered reachable interaction cell and capable negotiator,
then `UseCommsConsole` and the native trade dialog. Beacon stock eligibility and
drop-pod delivery belong to that native path; adjacent map-trade checks cannot
authorize it.

## Dependencies and retained cancellation

Dependencies distinguish orders issued from work complete. Stable step identities retain
receipts; changed intent requires a new identity. Duplicate intent and cancelled
fingerprints prevent recreating the same work under another ID. An unchanged cancelled
step can remain in subsequent plan revisions with its receipts intact; it is never ready
for execution. Changing or reintroducing that cancelled work is rejected. Cancelling a
plan step does not cancel existing game orders. Observation failures retain issued work
until fresh evidence arrives. Ambiguous non-idempotent writes block for inspection
instead of automatic replay; only explicitly retryable failures can be retried through
the plan.

## Related reading

See [spatial contracts](spatial-contracts.md), [sessions and
recovery](../architecture/sessions-and-recovery.md), and [plans and
Hands](../architecture/plans-and-hands.md).

### Auto production bill takeover

Auto reviews matching cooking, reserve and animal-butchery bills regardless of
ownership. Known suspension, finite repeat mode, insufficient targets, narrowed
ingredient filters and worker restrictions trigger a guarded replacement through
the existing production action. Unknown fields remain unknown. Adequate bills and
unrelated recipes remain; ordinary meal-tier replacement follows the meal review.
Same-recipe replacement stays on its original bench and works on a full stack.
The stack token protects the edit; completion still requires observed production.

The bill census reports whether ingredient definitions, ranges and special rules
match Auto defaults, along with worker-category/skill restrictions and the pinned
pawn. Ordinary cooking also reports its allowed definitions for explicit diets.
Butchery retains its separate human-corpse census and routing.

### Production configuration diagnostics

Native bill progress rejects a changed configuration or stack index even when
output was produced. Its unsuccessful detail names up to six changed fields
with captured and current values, an omitted-field count, and a 1536-byte ASCII
bound. Collection fields (ingredient definitions, special filters and storage
cells) use full SHA-256 digests; long or non-ASCII scalar values use digests too.
These diagnostics are emitted only for unsuccessful progress, not colony facts.
Native pause state remains outside the configuration hash.

Saving can change a bill: RimWorld's Bill.ExposeData prunes ingredient
definitions excluded by the recipe's fixed filter. Production/configuration
reproduces this for a Kibble bill and checks scalar and reorder rejection.
