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
| `home/install` through `native_operation` | Exact inner building identity at the intended destination/rotation. |
| Medical native operations | Optional `patient_tended` and `patient_in_bed` wait for fresh living-patient observations. These certify current treatment/delivery state, not full healing or actor attribution. |
| Waste native operations | Required `waste_contained` verifies the exact item in separated storage or a grave on a later native tick. Relocation does not mean destruction; explicit burial requires the body inside a grave. |
| `home/gear_upkeep` | `pawn_gear` requires fresh exact apparel/primary-weapon identity on the assigned pawn; an ordered job is insufficient. See [equipment upkeep](equipment-upkeep.md). |
| Population native operations | Orders/settings have receipt boundaries; [population goals](population-contracts.md) separately observe custody, care, recruitment and work/equipment/housing integration. Current-load observed custody/settings can resolve an uncertain order without replaying it. |

| `RequestSurgery` | `surgery_health` verifies the expected native condition change on the exact patient/body part. Bill removal does not certify success; postoperative recovery remains separate. See [medical care](medical-care.md). |
| Upkeep native operations | `upkeep_target` waits for protected stock, repaired target health, or cleaned target absence. Ownership, quantity and unknown-state rules are in [upkeep contracts](upkeep-contracts.md). |
| `trade` | Guarded open/stage/preview/accept with participant, content and silver-budget checks; hauling/storage remain separate. |
| Caravan formation and travel | Exact living crew with observed loaded departure, destination arrival or home-map return; assembly and route receipts alone do not complete travel. |
| Quest acceptance | Fresh scoped acceptance tick; native quest success remains a separate observed state. |
| `movement` (hold-the-line positioning) | Walk-to-cell order layered on the defender's owned draft, admitted only while the draft claim is current and native previews the `Goto` job. Completion is the verified job observed finished on a later native tick; the receipt alone proves nothing about arrival, and arrival certifies position, not combat outcome. The hold plan's ranged attack depends on the move. |
| `clock`, `stand_down` | Native clock control or verified release of selected current-load AI-owned drafts; neither certifies combat victory. |
| `building_temperature` (PatchBuilding target temperature) | CAS-gated setpoint patch on one exact `CompTempControl` building; the receipt's after-token must match a fresh building read. When `MaintainRefrigeration` commits it as a routine method, the patch completing never clears the goal: the stock's measured temperature must be observed at or under the release threshold on a later native tick. |
| `bed_medical` (PatchBuilding medical) | CAS-gated medical flag on one exact humanlike `Building_Bed`; the token covers the flag, `ForPrisoners` and the owner set, so an owner the planner did not see is a stale admission, and a definition the game cannot make medical is refused at preview. The patch completing places a bed, not a patient: `MaintainMedicalCare` still recovers only on observed tending and rest. |
| `grower_crop` (PatchBuilding plant_def) | CAS-gated crop on one exact `Building_PlantGrower`; the token covers the grower's current crop, and a plant the game's own set-plant gizmo would not list (not sowable, missing the grower's sow tag, sow research unfinished) is refused at preview. The patch completing changes what the grower sows next, not what grows in it: `EnsureFoodSupply` still recovers only on observed food coverage. |
| `bed_assign` (AssignBed) | CAS-gated ownership transfer of one exact vacant humanlike `Building_Bed` to one exact colonist, carrying the pawn's expected previous bed (or none); the pawn token covers dead/downed/drafted/in-bed state and the bed token its status, so a bed claimed or damaged since the review is a stale admission, and the native side re-vets roof, forbidden state, allowed area, reach and the pawn's comfortable temperature band before `TryAssignPawn`. The receipt carries the ownership readback and observation confirms it, but `MaintainSleeping` recovers only on the pawn's observed sleep in that bed. |

## Apply-time preconditions and refusal reasons

A routine write carries the snapshot token the controller read at a stopped
tick, but the token is not the check. Every kind below re-evaluates its
preconditions inside the operation, on the main thread, at the tick the
write applies (nothing ticks between the check and the effect), in the order
listed; the first rule that fails is the refusal, so a world that moved under
the order names the fact that moved. The token comparison is always the last
rule: it still catches a change no rule names, and dropping it (#240 phase 3)
leaves the rules untouched. A refusal is a terminal failure reply
(`InvalidRequest`; `NotFound` where the exact target is gone), never a
receipt: the worker reconciles it as `ReceiptRefused`, flight.jsonl keeps the
detail verbatim, and no Go code parses it. The detail is
`<Kind> refused: <reason>`; the `apply/refusal` case executes one write per
kind after moving the world under a token that was valid when read.

| Kind | Preconditions, in order (the reason text is the rule that failed) |
| --- | --- |
| Zone creation (`CreateZone`) | Growing zones, per cell: `cell (x, z) is out of bounds or fogged`, `is not walkable`, `is outside the crop's growing season`, `holds a building, blueprint or frame`, `is already zoned`, `is marked for roof collapse`, `is not fertile enough for the crop`, `is refused by the native growing-zone designator`. Stockpiles, per cell: `fresh free ground required: cell (x, z) is not roofed, walkable, unzoned, empty storage ground`. Then `the map's zone census changed since it was read`. |
| Zone cell edit (`EditZoneCells`) | `the exact zone no longer exists on this map` (NotFound); `the zone holds a phantom cell the zone grid does not map to it`; `the stockpile's haul grid no longer matches its cells`. Adding, per cell: `cell (x, z) is already in the zone`, `is not free zoneable ground`, `is not roofed, empty and clear` (covered-empty edits), `already belongs to a storage group`. Removing, per cell: `cell (x, z) is not in the zone`, `is not mapped to the zone on the zone grid`, `is not mapped to the stockpile's storage group`. Then the contiguity rule (`allow_split`) and `the zone snapshot changed since it was read`. |
| Zone deletion (`DeleteZone`) | `the exact zone no longer exists on this map` (NotFound); the phantom-cell and haul-grid rules above; `the zone snapshot changed since it was read`. |
| Stockpile patch (`PatchStockpile`) | `the exact stockpile zone no longer exists on this map` (NotFound); `the stockpile has no storage settings`; `the settings body does not resolve against the stockpile's storable definitions`; `the stockpile snapshot changed since it was read`. |
| Allow (`DesignateThing` allow) | `the exact item is no longer spawned on this map` (NotFound); `the item is not a forbiddable haulable item`; `the item's cell is fogged`; `the item belongs to another faction`; `the item is already allowed`; `the native unforbid designator refuses the item`; `the item snapshot changed since it was read`. |
| Haul (`PawnTargetOrder` haul) | `the exact pawn is not spawned on this map` (NotFound), then the draft-protocol control refusal; `the pawn is dead or downed`; `the pawn is in a mental state`; `the pawn is not a player-controlled colonist`; `the pawn is drafted`; `the exact haul target is no longer spawned on this map` (NotFound); `the target is not a haulable item`; `the target's cell is fogged`; `the target snapshot changed since it was read`; `the pawn snapshot changed since it was read`. The native `JobFailReason` of a refused job follows the same channel (#189). |
| Work settings (`PatchPawn` work/area) | `the exact pawn is no longer spawned on this map` (NotFound); `the pawn is not a living free colonist`; `the pawn is downed`; `the pawn is drafted`; `the pawn is in a mental state`; `the pawn has no work settings`; `the game's manual-priorities setting is unreadable`; `a requested work type is not defined`; `a requested work type is disabled for the pawn`; `manual priorities are off, so only 0 or 3 can be set`; `the requested allowed area is not on the pawn's map`; `the pawn's work/area snapshot changed since it was read`. |
| Plant acquisition (cut/harvest, `AcquireResource` on a plant) | `a roof collapse is pending on this map`; `the exact plant is no longer spawned on this map` (NotFound); `the plant is not at the expected cell`; `the plant no longer yields the expected resource`; `the plant's cell is fogged`; `the plant is forbidden`; `the plant is not harvestable now`; `the plant stands in a growing zone`; `the plant is already designated`; `the native designator refuses the plant`; `no free colonist with plant cutting enabled can reach the plant`; `the plant snapshot changed since it was read`. |
| Mine (`AcquireResource` on a rock) | `a roof collapse is pending on this map`; `the exact rock is no longer spawned on this map` (NotFound); `the rock is not at the expected cell`; `the rock no longer yields the expected resource`; `the rock's cell is fogged`; `the rock is forbidden`; `excavation geometry is unsafe: <blocker>`; `the rock is already designated for mining`; `the native mine designator refuses the rock`; `no free colonist able to mine can reach the rock`; `the rock snapshot changed since it was read`. |
| Hunt (`AcquireResource` on an animal) | `two hunts are already outstanding on this map`; `a roof collapse is pending on this map`; `the exact animal is no longer spawned on this map` (NotFound); `the animal is dead or downed`; `the animal is not safe wild prey`; `the animal is not at the expected cell`; `the animal's corpse is not the expected resource`; `the animal's cell is fogged`; `the animal is already designated for hunting`; `the native hunt designator refuses the animal`; `no usable butcher bill with an assigned cook accepts the corpse`; `no free colonist with hunting enabled and an ordinary ranged weapon has a safe route to the animal`; `the animal snapshot changed since it was read`. |
| Tame and the other husbandry writes | `Exact wild animal is unavailable.` / `Exact eligible player animal is unavailable.` (NotFound); the per-kind eligibility refusal (`Native tame eligibility refused the animal or it is already designated.`, the slaughter, release, training, area, master and following rules); then `Animal settings or census changed; observe before new admission.` |
| Bills (`AddBill`) | `Production bill requires unchanged native bench, available recipe and assigned skilled worker: ` + `bench <id> is not a loaded bill giver`, `bench is not usable for bills`, `bill stack is full`, `bench already carries a <recipe> bill`, `recipe <recipe> is not available on the bench`, `recipe <recipe> has no work type on <bench>`, `no free colonist works <work type> within reach of the bench with the recipe's skills (...)`; then `bench bill stack changed since it was read`. |
| Build (`PlaceBuilding`) | The placement is re-planned at execute with the same diagnostic the preview reports (`NativeConstructionPlan.Prepare`): footprint, terrain, stuff, reach and blocking-thing rules refuse with the preview's own reason text; an admitted plan then goes through the construction ledger. |
| Grower crop (`PatchBuilding` plant_def) | `Exact plant grower is unavailable.` (NotFound); the sowability rule; `Grower crop snapshot changed; observe before new admission.` (StaleIdentity). |
| Deconstruct (`RemoveWall`) and excavate (`ExcavateCell`) | Pause-bound (`Paused loaded map required.`): the exact wall or visible rock, roof-collapse and roof-support geometry, an eligible worker, then `Wall snapshot changed` / `Rock snapshot changed; observe before new admission.` Their pause-bound admission is outside #242. |

Roof areas have no native write kind; combat/draft and medical writes keep
their own protocols. Forbid (`DesignateThing` forbid) has no native handler
yet and follows the Allow list when it lands.

## Trades

Map trades require a paused game and a reachable, eligible negotiator adjacent to
the trader. Native sessions bind the exact deal, participants, map and colony load.
Set/cancel/accept requests carry the session ID; acceptance also carries the preview
signature covering exact rows, counts, stock identities and prices. Native acceptance
rechecks stock eligibility, both silver balances and trader availability after any
viewing delay. A lost receipt retains the shared Hands uncertain-write marker across
restore and cannot replay. Bought map goods appear at the carrying trader/pack
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
acceptance takes a trader quest. Trade is routine-only; there is no player trade command.

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
