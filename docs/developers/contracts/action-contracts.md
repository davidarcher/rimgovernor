# Action completion contracts

[Documentation](../../README.md)

An accepted native order and a completed action are separate states. The table defines
what each action must observe. Kinds marked intent-mode complete on an applied receipt
alone (no observation phase); a refusal fails them and a lost receipt sends the intent again.
Field-level shapes live in `contracts/proto/*.md` and the proto files; native checks live in
the bridge and native code. This page keeps only the completion boundary and the
non-obvious constraints.

| Committed action | Completion boundary |
| --- | --- |
| `build_room_shell`, `place_buildings` | Native building observations through ProjectBook; a shell does not certify roofing or usable shelter. |
| `create_zone` | Validated native zone geometry/readback; storage and crop production are separate outcomes. |
| `native_operation` | Schema-validated native receipt/readback by default. Bills, settings, designators and UI actions do not imply downstream pawn labor finished. |
| Existing-zone edits | Fresh native geometry, crop or filter must match the requested edit; deleted zones must be absent. |
| Bill ingredient whitelists | Exact native bill identity and filter must match fresh readback; production remains separate. |
| Medical native operations | Optional `patient_tended` and `patient_in_bed` wait for fresh living-patient observations. These certify current treatment/delivery state, not full healing or actor attribution. |
| Waste native operations | Required `waste_contained` verifies the exact item in separated storage or a grave on a later native tick. Relocation does not mean destruction; explicit burial requires the body inside a grave. |
| Population native operations | Orders/settings have receipt boundaries; [population concerns](population-contracts.md) separately observe custody, care, recruitment and work/equipment/housing integration. Current-load observed custody/settings can resolve an uncertain order without replaying it. |
| Upkeep native operations | `upkeep_target` waits for protected stock, repaired target health, or cleaned target absence. Ownership, quantity and unknown-state rules are in [upkeep contracts](upkeep-contracts.md). |
| `trade` | Guarded open/stage/preview/accept with participant, content and silver-budget checks; hauling/storage remain separate. See [Trades](#trades). |
| Caravan formation | Exact living crew with an observed loaded departure; an assembly receipt alone does not complete formation. |
| Quest acceptance | Fresh scoped acceptance tick; native quest success remains a separate observed state. `MaintainPopulation` also accepts Empire quests through `QuestAccept`: not yet accepted, non-joiner, `can_accept`, no required accepter, non-hostile `faction_id`, `map_id` equal to the identity map and a reward choice with `favor > 0` (highest favor is the reward index, lowest quest ID breaks a tie). Odyssey quests are accepted only when the catalog classes the script ground (`bridge.QuestClass`); skipped ones log `odyssey quest skipped <quest> <script>: <reason>`. |
| Ability use (`ability`, `AbilityIntent`) | Receipt asserts the native postcondition per source. Permit: `PermitUseEffect` with `cooldown_started_tick` equal to the apply tick and `favor_after == favor_before - cost`; aid arrival and strike results are separate observed states. Psycast: `PsycastUseEffect` with the ability and the `job_def` of the cast job the pawn holds after `Ability.QueueCastingJob`; psyfocus, entropy and cooldown are separate observed state. A use that already took effect is never re-sent (retry refuses `cooldown`, a pending psycast refuses `casting`); an uncertain receipt is resolved by the next frame's pawn rows (`bridge.PawnRoyaltyFacts`, `RoyaltyFacts.PermitUsedSince`). |
| `ignite` (`IgniteIntent`) | One drafted colonist force-fires the molotov in the primary slot at one cell (`CombatOrder` attack_ground); refused while any pawn is inside the target room. No firebreak or stock check. Applied (`CombatOrdersEffect`) means the throw was ordered; whether the fire caught is a separate observed state. |
| `movement` (`MoveIntent`) | Walk-to-cell for a plan-drafted pawn: applied when the pawn is alive, spawned, drafted and the cell is standable and reachable; an order that already matches is a no-op. Terminal; says nothing about arrival. A shrine plan holds its final action until every mover stands on its cell, re-sending the move of a drafted pawn idle elsewhere. |
| `clock` | Native clock control; it certifies no combat victory. |
| `draft` (`DraftIntent`) | Sets or clears one colonist's draft under Auto. Drafts are plan-owned: no native claim, and the undraft sweep undrafts every drafted colonist no live plan needs. |
| `building_temperature` (`BuildingPatchIntent` target_temperature) | Setpoint on one exact `CompTempControl` building, -273.15..1000 C. Applied means set; the next building read confirms it. |
| `bed_medical` (`BuildingPatchIntent` medical / for_prisoners) | Medical flag on one exact `Building_Bed`, or prisoner use on a humanlike one; the game's setter drops every owner. A bed def that cannot be medical, or a crib / non-prison room for prisoners, is refused. |
| `grower_crop` (`BuildingPatchIntent` plant_def) | Crop on one exact `Building_PlantGrower` under the set-plant gizmo rules (sowable, grower's sow tag, sow research finished). |
| `claim_building` (`BuildingPatchIntent` claim) | `Building.ClaimableBy(player)` then `SetFaction(player)` on one exact building; already the player's applies again. Nothing opens a casket. |
| `auto_refuel` (`BuildingPatchIntent` auto_refuel) | Sets `allowAutoRefuel` on one exact player building whose `CompRefuelable` shows the toggle; a held setting applies again. The temperature family lets a heat campfire burn out once its sleeping room reaches 26 C and refuels it below 16 C. |
| `use_item` (job `UseItem`) | `GiveJobIntent` with targets `[item, target pawn]`: a target-effect verb (shock lance) or `CompTargetable` item on another pawn, or, when the pawn is its own target, the use job of a `CompUsable` item without a target comp (psylink neuroformer): `CanBeUsedBy` live, then `TryStartUseJob`. Only this job admits the pawn among its targets. Applied means ordered; the hediff or psylink is the next census. |
| `drop_equipment` (job token `DropWeapon`) | `GiveJobIntent` with one target, the weapon in the pawn's hands. `DropWeapon` is this protocol's token, not a JobDef name: native finds the JobDef whose driver is `JobDriver_DropEquipment` and fails with a plain-English error when the game has none or several. No draft gate; a pawn already dropping, or whose weapon already lies on the ground, applies again. Applied means ordered. Unverified in game. |
| `prioritize_slaughter` (job `Slaughter`) | Husbandry-plan method: prioritized `GiveJobIntent` (one target, the designated animal) by the best Animals-skilled Handling-capable colonist (`TamerFor`) once `HusbandryIntent` slaughter stands; the colonist need not be drafted. Native validates the work giver live. No capable handler leaves the designation to native handlers. |
| `open_casket` (job `Open`) | `GiveJobIntent`: vanilla `Open` job by one eligible colonist, drafted or not, on one exact filled `Building_AncientCryptosleepCasket`; native adds the `Open` designation right before the job and removes it if the job ends with the casket still full. Completion is the casket observed empty; opening one ejects every casket of its shrine group, so a plan carries one order. Refused when empty, reserved, unreachable or the pawn cannot do dumb labor. Behind the [opening gate](controller-contracts.md#ancient-shrine-breach-concern). |
| `move_building` (`RelocateIntent`) | Re-sites one exact installed, minifiable player building through Reinstall (`GenConstruct.PlaceBlueprintForReinstall`, no `WipeExistingThings`). Construction work carries the piece and needs `CanReserve`, so a sleeping or working pawn is never interrupted. A packed item (own id or inner building's id, `inner_id`/`inner_def_name` in `observations_list_supplies`) gets `PlaceBlueprintForInstall`. Applied (`QUEUED`, blueprint id) means ordered; a blueprint already standing for the same placement applies again. |
| `uninstall_building` (`RelocateIntent` with `uninstall`) | Vanilla Uninstall designation (an existing one applies again); construction work minifies it, vanilla hauling stores it (native never hauls). Applied (`UNINSTALL_QUEUED`) means ordered. |
| `area` (`AreaIntent`) | Creates, edits or deletes one bot-owned `Area_Allowed` labelled with its key, or sets/clears home-area or pollution-clear-area cells (`create`, `set_cells`, `clear_cells`, `delete`). Player-made areas are never touched. Applied (`AreaEffect`) carries the area load id and cell count. Proven by `apply/area-intent`. |
| `wastepack_haul` (`DesignateIntent` HAUL, `DESIGNATION_GUARD_WASTEPACK`) | Designates one exact wastepack for hauling: a spawned `CompDissolution` thing that is neither frozen nor inside an atomizer, not forbidden, with an accepting stockpile (allow a forbidden pack first through `supply_allow`). A standing designation applies again; evidence is a `DesignationEffect`. |
| `policy_prune` (`PolicyPruneIntent`) | Deletes outfit, drug, food or reading policies, or allowed areas, by load id; the bot owns every one, player-made included. Every pawn still holding one first moves onto its own per-pawn policy (made from defaults when missing) or becomes unrestricted, since `TryDelete` refuses a policy in use. An id already gone applies again. Applied (`PolicyPruneEffect`) lists ids deleted and pawns moved. Proven by `apply/policy-prune`. |
| `floor_removal` (`RemoveFloorIntent`) | Designates one cell's constructed floor for removal; natural terrain is refused; a designated cell or one without that floor applies again. |
| `remove_roof` (`RemoveRoofIntent`) | Marks canonical cells in the NoRoof area; clearance issues it over an enclosed room's roofed cells before deconstructing walls. Refuses a fogged or out-of-bounds cell or a thick roof; already unroofed or designated applies again. Applied (`RemoveRoofEffect`) means designated; the roof read decides when pawns finished. |
| `ritual` (`RitualIntent`) | Player command for an Ideology/Royalty ritual; a new ritual adds a ritual kind, not an action kind. Verbs below. |
| `area_plant_cut` (`AreaPlantCutIntent`) | Orders every non-crop plant on canonical cells cut (the firebreak ring): wild plants CutPlant, harvestable trees chop-wood; growing-zone and sown-crop plants are never touched; no cover-fill requirement. Applied (`AreaPlantCutEffect`) means designated; `ReadPlantCutCensus` decides when pawns finished and when to sweep again. Ready work: plant cutting, one cell claim per cell. |
| `production_bill` with `patient` (medical bill) | Queues one medical operation bill on a colonist, slave or colony prisoner via `HealthCardUtility.CreateSurgeryBill`: the recipe on the part at `part_index` in the race body's `AllParts` (absent for whole-body). A violation recipe (organ harvest) needs `acknowledge_violation`. Applied (`SurgeryEffect` `QUEUED`, `Bill_Medical` id) means queued; the same recipe on the same part applies again; native doctors choose the surgeon unless `surgeon` restricts it. Proven by `medical/surgery-intent`. |
| `production_bill` for a mech recipe | Queues one `Bill_Mech` (`REPEAT_MODE_COUNT`, count 1, no worker, ingredients or replacement) on a `Building_MechGestator`. A mech recipe produces a mechanoid race (catalog mech kind row; `MechKindRow` gives the bandwidth cost). Needs a usable gestator, the recipe available, and a free mechanitor whose `TotalBandwidth - UsedBandwidth - UsedBandwidthFromGestation` covers the cost, re-checked at apply via `HasBandwidthForBill`. A standing bill applies again. Applied (`BillEffect`) means queued. Unverified in game. |
| `pawn_settings` (`PawnSettingsIntent`) | One per-pawn Assign-tab setting on one colonist per intent; see [Pawn settings](#pawn-settings). |
| `drug_policy` (`DrugPolicyIntent`) | Writes one drug policy: the `DrugPolicy` labelled `name` (made when missing) carries exactly `entries` and every other drug is off; refused when several policies carry the name or an entry names a drug the policy does not list; a matching policy applies again (`SettingsEffect` DRUG_POLICY). `EnsureWorkAssignments` gives every colonist with a drug tracker and unique short name the policy of that name: joy drugs unless addicted or tolerance >= 0.5, none for children (under 13), teetotalers or pawns with a chemical interest; Penoxycyline where the biome has Malaria or Plague (`PolicyFacts.biome_diseases`); each dependency gene's drug (`dependency_chemicals`). Each addiction adds a scheduled entry for its first drug in the item census: weaned (interval widened by 1/severity up to 4x) when stock left after earlier pawns covers 30 days x severity of doses, else maintained; luciferium is always maintained. Proven by `pawn/drug-policy` and `pawn/drug-addiction`. |
| `food_policy` (`FoodPolicyIntent`) | Writes one food policy: the `FoodPolicy` labelled `name` allows exactly `allowed_defs` among foods (nutrition ingestibles that are no drug or corpse); special filters keep their values, every corpse is disallowed; refused when several policies carry the name; a matching policy applies again (`SettingsEffect` FOOD_RESTRICTION). `EnsureWorkAssignments` derives each colonist's policy from traits and ideoligion precepts. Slaves and prisoners get only nutrient paste and raw food within that diet; tame animals kibble, hay and the raw food their race eats (`PolicyFacts.food_eaters`, catalog `edible_defs`). `DefinitionCatalog.Foods` classifies foods from the catalog's `thing_facts`. Proven by `pawn/food-policy` and `pawn/animal-food-policy`. |
| `reading_policy` (`ReadingPolicyIntent`) | Writes one reading policy: the `ReadingPolicy` labelled `name` allows exactly `allowed_defs` (book ThingDefs) and every book effect; refused when several policies carry the name; a matching policy applies again (`SettingsEffect` READING_POLICY). `EnsureWorkAssignments`: a child textbooks only; an adult novels, textbooks with a passion, schematics when researching; Anomaly tomes never. `DefinitionCatalog.Books` classifies books by outcome doers. Proven by `pawn/reading-policy`. |
| `auto_home_area` (`AutoHomeAreaIntent`) | Sets `Find.PlaySettings.autoHomeArea` (save-level). `SettingsEffect` `AUTO_HOME_AREA`: `APPLIED` when changed, `UNCHANGED` when held; `UpkeepFacts.auto_home_area` reads it. Proven by `upkeep/auto-home-area`. |
| `assign` (`AssignIntent`) | Ownership transfer of one exact assignable thing (anything with `CompAssignableToPawn`: bed, throne, sarcophagus) to one exact colonist, carrying the pawn's expected previous assignment of that kind. Native checks at apply that the pawn is free (not dead, downed, drafted or in a mental state) and the expected previous assignment still holds, then the kind's rules before `TryAssignPawn`: for a humanlike bed its roof, forbidden state, allowed area, reach and the pawn's comfortable temperature band (`swap` evicts owners); for a throne or grave no other owner, forbidden state and fire; plus the comp's own `CanAssignTo` and ideology checks. A pawn already owning the thing applies again. An ideology role is the same intent with `thing_id` the role precept id: native checks the role is active, the pawn a living free colonist of the same ideoligion, requirements met (`GetFirstUnmetRequirement`) and a place free (`maxCount`), unassigns the expected previous role, then `Precept_Role.Assign`. The result carries `AssignEffect`; for a bed `MaintainHousing` recovers only on observed sleep in it, for a throne the royalty read's `thrones` owner closes the step. |
| `Arrest` | Vanilla custody via `JobDefOf.Arrest`: an armed, violence-capable drafted arrester (plan-owned draft), an exact target in a mental state or a standing neutral `Faction.OfAncients` humanlike, and an exact usable prisoner bed. Native rechecks eligibility, manipulation, reservation and reach; hostile aggressive states such as Berserk are refused. Optional pawn/target/bed tokens fence stale snapshots. Completion requires a later observation of the living target as a colony prisoner in that exact bed with its mental state ended. Resistance or interrupted delivery is unsuccessful; admission, mental-state recovery alone and custody before bed delivery do not complete it. `mood/arrest` proves refusals, replay and a legal sad-wander arrest. |

### Ritual verbs

`pawn` is the colonist the ritual is for, `ritual` a closed kind (`bestowing`) or, for `begin`, a precept id.

| Verb | Contract |
| --- | --- |
| `start` | Starts a ritual whose lord waits for it. MaintainPopulation issues it (`policy.CeremonyStart`) for an accepted ceremony whose bestower is known to wait and whose ritual is known not to have started; an unknown flag emits nothing. Native runs the action of the `Command_BestowerCeremony` gizmo the bestower's `LordToil_BestowingCeremony_Wait` offers (no spectators chosen). Refusals: unknown colonist or no accepted ceremony with a bestower lord (NotFound); unsupported ritual or verb, bestower not spawned or not in the Wait toil, no gizmo or a disabled one (InvalidRequest). A ritual already started applies again. Postcondition (`RitualEffect.started`): the lord job's `ceremonyStarted` or the toil left Wait. An instant native write: no slot or labor. |
| `begin` | Starts a held Ideology ritual precept with no lord waiting. `ritual` is the `Precept_Ritual` load id (`IdeoRitual.id`), `pawn` the organizer, `spot` the target cell, `roles` the exact fills of the behavior's role slots (slot id from `RitualRole.id`, to pawns in priority order) and `spectators` the pawns without a role; every pawn appears once and nothing is auto-filled. `domain.NewRitualBegin` refuses an invalid organizer, precept or slot id, a negative spot, a repeated or empty slot and a pawn named twice. Native (`NativeRitualBegin`) takes the `Command_Ritual` the precept offers at the spot, replaces the dialog's assignments with the intent's (`RitualRoleAssignments.TryAssign`, `TryAssignSpectate`), then confirms. Refusals: unknown or non-colonist organizer, no ideoligion, unknown precept, a role slot the ritual lacks, or a pawn the game does not offer as a candidate (NotFound); no command at the spot, the command disabled, `CanStartRitualNow` or the dialog's blocking issues, organizer not on a map or spot off it (InvalidRequest). Postcondition (`RitualEffect.started`): a `LordJob_Ritual` of the precept is running; one already running applies again. Planner: MaintainRituals (`policy.PlanRituals`, [ideology contracts](ideology-contracts.md#ritual-scheduling)). |

### Pawn settings

One setting per `PawnSettingsIntent`; the effect is a `SettingsEffect` of the named kind. Proven by `pawn/hostility`, `pawn/self-tend`, `pawn/nickname`, `pawn/medicine-carry`, `pawn/reading-policy`, `pawn/drug-policy` and `pawn/food-policy`.

| Setting | Contract |
| --- | --- |
| `hostility_response` (HOSTILITY) | `playerSettings.hostilityResponse`; Attack is refused for a violence-incapable pawn. `EnsureWorkAssignments` writes Attack for fighters, Flee for violence-incapable pawns, children and pawns with blood loss >= 0.3 or summary health < 0.6, and Ignore while a work-giver job targets a cell within 15 cells of a known-passive hostile (a sleeping hive) with no engaging hostile near. |
| `self_tend` (SELF_TEND) | `playerSettings.selfTend`, refused for a pawn that cannot doctor. On when the pawn's MedicalTendQuality x0.7 is at least the best other available non-guest doctor's, or there is none. |
| `nickname` (NICKNAME) | Names the short name an owned pawn must leave: native draws a fresh name from the pawn's name bank, never one with a digit or held by another owned pawn; a humanlike keeps first and last name. `EnsureWorkAssignments` renames every pawn whose short name an older owned pawn (lower thingIDNumber) holds, from the population read's `owned_names`. |
| `medicine_carry` (MEDICINE_CARRY) | Medicine `inventoryStock` count (0-3) with the best medicine the pawn's `medCare` allows (a positive count is refused when it allows none). Doctor-enabled colonists get 1-3, hunters or armed ranged fighters 1-2, one unit a round while colony stock plus carried stays at the medical reserve target; others 0. |
| `reading_policy`, `drug_policy`, `food_policy` | Assigns the one policy of that kind labelled with the given name; contents come from the matching `*_policy` kinds. |
| `mech_work_mode`, `mech_control_group` | For a controllable colony mechanoid with a living mechanitor overseer: the first sets the `MechWorkModeDef` of the mech's control group, the second moves the mech into the overseer's group with that index. Other factions, non-mechs, no overseer, unknown mode or index past the overseer's groups are refused. Move the mech first, then set the group's mode. Read back via `PawnMech.work_mode` / `control_group`. |
| `choose_permit` (PERMIT) | The royalty write: the `faction_def` and `permit` def the colonist takes. MaintainPermits commits `policy.NextPermit`; tried at most `maxMedicalAttemptsPerPatient` times per holder and permit per Episode. Native applies the permit window's checks then `Pawn_RoyaltyTracker.AddPermit`. Refusals: unknown colonist, faction or permit def (NotFound); no title with the faction, a permit of another faction, title below the minimum, missing prerequisite or too few permit points (InvalidRequest). Read back: the permit is held and the faction's permit points dropped by its cost. |

## Apply-time preconditions and refusal reasons

A routine write carries the snapshot token the controller read, but the token is not the
check. Every kind below re-evaluates its preconditions inside the operation, on the main
thread, at the tick the write applies, in the order listed; the first rule that fails is
the refusal. The token comparison is always the last rule: it still catches a change no
rule names. The acquisition kinds (plant, mine, hunt) accept a request without a token,
and the controller's execute omits it: the worker dispatches them under a running clock,
where the token moves every tick, and the rules alone refuse a moved world; a preview
still sends it. A refusal is a terminal failure reply (`InvalidRequest`; `NotFound` where
the exact target is gone), never a receipt: the worker reconciles it as `ReceiptRefused`,
flight.jsonl keeps the detail verbatim, and no Go code parses it. The detail is
`<Kind> refused: <reason>`; the `apply/refusal` case executes one write per kind after
moving the world under a valid token. The reason text itself lives in the native handler
for each kind (grep the quoted phrase); the table names the rule order and the non-obvious
rules.

An acquisition inspection outrun by the live clock records a stale_facts hold and
invalidates its cached reads. The worker gives it one immediate fresh retry before
ordinary backoff. A stale inspection never dispatches a write; native ineligibility
remains an ordinary hold.

Kinds marked "No snapshot token" carry none. "Applies again" means an already-held state
returns applied rather than refused.

| Kind | Preconditions, in order |
| --- | --- |
| Zone creation (`ZoneIntent` create; the zone already standing applies again, with its id) | Growing zones, per cell: out of bounds or fogged, not walkable, outside the crop's growing season, holds a building/blueprint/frame, already zoned, marked for roof collapse, not fertile enough, refused by the native growing-zone designator. Stockpiles, per cell: `fresh free ground required` (roofed, walkable, unzoned, empty storage ground); a filter admitting only no-deterioration things (a chunk dump) drops the roof rule. Then, only when the intent carries the planner's census token, `the map's zone census changed since it was read`. |
| Zone cell edit (`ZoneIntent` cells; cells already in/out apply again) | Exact zone gone (NotFound); phantom cell the zone grid does not map to it; stockpile haul grid no longer matches its cells. Adding, per cell: not free zoneable ground, not fishable water in the zone's water body, already in a storage group. Removing, per cell: not mapped to the zone or its storage group. Then `the edited zone would not be contiguous`. |
| Zone deletion (`ZoneIntent` delete; a gone zone applies again) | The phantom-cell and haul-grid rules above. |
| Stockpile settings (`ZoneIntent` settings; held settings apply again) | Target is a stockpile zone or player `Building_Storage`: gone (NotFound); no storage settings; settings body does not resolve against the target's storable definitions. |
| Allow / Forbid (`DesignateIntent`) | Item no longer spawned (NotFound); not a forbiddable haulable item; cell fogged; belongs to another faction; the native forbid designator refuses. Already in the wanted state applies again. Applied is terminal, refused replans. |
| Cut blighted plant (`DesignateIntent` cut_plant) | Plant gone (NotFound); not blighted; cell fogged; outside growing zones and home area; forbidden; native cut designator refuses; no free plant-cutting colonist can reach it. Already designated applies again. Applied is terminal, refused replans; the concern settles on the census. |
| Removal designation (`DesignateIntent` with `target` or `cell`) | `Designate refused:` designation must be Deconstruct, Mine, CutPlant, Haul or RemoveFoundation; Deconstruct requires the enclosure guard, Mine the `mine_safety` guard, others take none; target gone (NotFound); not at the expected cell; expected rock not visible; forbidden; the guard's refusal (enclosure: player-deconstructible, visible geometry, not burning, enclosing colony wall, cleared ground, no pending wall upgrade, roof support; mine_safety: cell blocker, roof support); carries another designation; no stockpile accepts the thing (Haul); native designator refuses. A standing designation (a player one adopted) or cleared cell applies again. The guard is re-checked before each deconstruct removal or mining pick: a failure drops the designation and ends the job (logged as a `guard cancelled` warning), cleared-ground roof still up holds pawns (`waiting_for_roof`), and a pending roof collapse holds a mine pick without dropping the designation. `replace_with_wall` swaps a 1x1 player door for a wall of its stuff. Revoking authority releases every guarded designation. |
| Haul (`HaulIntent`) | Pawn dead, downed, in a mental state, not a player colonist or drafted; item gone, not haulable, not spawned, fogged; no hauling job available. A pawn already hauling or carrying the item applies without a new order. Applied means ordered, not delivered. |
| Work settings (`WorkSettingsIntent`; held settings apply again) | Pawn gone (NotFound); not a living free colonist (or, care-only, a colony prisoner); prisoner without medical care settings; downed (care-only exempt); drafted; mental state; no work settings; manual-priorities setting unreadable; work type undefined or disabled for the pawn; manual priorities off so only 0 or 3; allowed area missing, unreachable or unsafe under the roof hazard; timetable undefined or pawn has none; no medical care settings. |
| Plant acquisition (HARVEST_PLANT Designate, `acquisition` guard) | Roof collapse pending; plant gone (NotFound); not at the expected cell; no longer yields the expected resource; fogged; forbidden; not harvestable now; in a growing zone; already designated; native designator refuses; no free plant-cutting colonist can reach it. |
| Mine (MINE Designate, `acquisition` guard) | Roof collapse pending; rock gone (NotFound); not at the expected cell; no longer yields the expected resource; fogged; forbidden; `excavation geometry is unsafe: <blocker>`; already designated; native designator refuses; no free miner can reach it. |
| Hunt (HUNT Designate, `acquisition` guard) | Roof collapse pending; animal gone (NotFound), dead, neither safe wild prey nor a recognised pest, corpse not the expected resource, fogged, already designated; native designator refuses. Butcher bill, hunter, weapon and the outstanding-hunt cap are policy (`policy.HuntGate`), not refusals. |
| Acquire (`DesignateIntent`, `acquisition` guard) | Acquisition, mine acquisition and `acquisition_withdraw` dispatch one Designate whose designation is the source's (HUNT for a corpse resource, MINE for a mine acquisition, otherwise HARVEST_PLANT); a mismatch refuses. Designation runs the rules above; an already designated source applies again. With `withdraw` the designation is removed (a hunter already on the prey is stopped); an undesignated or gone source applies again. The acquisition and medical planners withdraw any designated census row of the concern's kind left untaken past its stall deadline, the player's own included, as a one-action method admitted before re-selection. |
| Husbandry (`HusbandryIntent`; a held order applies again) | Order needs an animal and its argument; tame needs a wild animal on the map, every other order a player animal (NotFound); train: cannot train the def or designated for removal; slaughter/release/tame: native eligibility refused; area: cannot carry an area, area not on its map (NotFound), must preserve hazard protection and reachability; master/following: need learned Obedience, master a spawned free colonist on the map (NotFound); sterilize: no whole-body recipe or unavailable now. |
| Production bill (`ProductionBillIntent`; a bench already carrying a matching bill applies again) | Bench not a loaded bill giver (NotFound); tracking unavailable; bench unusable; replacement must be the same recipe on this bench or an ordinary meal tier; stack full; recipe unavailable on the bench, not ordinary single-product work, or lacking a work type; Beer reserve needs a wort recipe; ingredient filter does not fund the slots; no free colonist with the recipe's skills within reach. Applied means the bill stands; production is not observed. |
| Build (`PlaceBuilding`) | Re-planned at execute with the preview's own diagnostic (`NativeConstructionPlan.Prepare`): footprint, terrain, stuff, reach and blocking-thing rules refuse with the preview's reason text; an admitted plan goes through the construction ledger. |
| Grower crop, pawn settings, medical and mech bills, auto home area, uninstall, claim, waste haul, service order | Guards follow the order of the matching row in the first table: target gone (NotFound), then eligibility (ownership, reachability, a free eligible undrafted worker), then the setting's own validity; the exact text is in the native handler. Single-setting kinds with no snapshot token refuse a malformed intent as InvalidRequest. |
| Move (`RelocateIntent`) | Building not installed on this map (NotFound); not the player's, cannot be uninstalled, not rotatable (only north valid), already at the destination, already has a reinstall blueprint elsewhere, designated for uninstall or deconstruction, destination out of bounds or fogged, `GenConstruct.CanPlaceBlueprintAt` refuses, no free construction colonist can reach it. Packed: item not on this map (NotFound), not the player's, fogged or forbidden, install blueprint elsewhere, game refuses at the destination, no reachable worker. No snapshot token. |
| Remove wall (`DesignateIntent` DECONSTRUCT, `wall_upgrade` guard) | Loaded map required; cell outside the map; a different wall stands there (StaleIdentity, when `target` names another wall); the site blocker (enclosure, roof support, an eligible worker) or no wall-upgrade site; several admissible sites share this wall. No colonist wall, or a wall this controller already designated, applies again. No snapshot token. |
| Ability use (`AbilityIntent`; `source` is a oneof: permit, psycast) | `Ability guard <name>: <reason>`. Shared: pawn id and target arm required (InvalidRequest); pawn not spawned (NotFound), not a living free colonist, or downed (a drafted pawn may call); unsupported source: Unsupported. Each source registers an ordered list of named guards calling the game's own validation; favor cost, cooldown and range are read natively, never from Go. Permit guards (only targeted `royalAid` workers: CallAid, OrbitalStrike, DropResources, CallShuttle): `permit`, `held`, `cooldown`, `faction`, `favor`, `aid` (an enabled `GetRoyalAidOptions` option; a disabled one's reason is the detail), `target` (cell in map, not fogged, within `royalAid.targetingRange`, line of sight when required). Psycast guards (`psycast:<abilityDef>`): `ability`, `cooldown`, `casting`, `target` (exactly the arm the ability's targeting takes; destination or world targets Unsupported), `psyfocus`, `entropy`, `cast`, `range` (the pawn does not walk into range; self-cast skips it), `confirmation` (Unsupported). Apply runs the aid option or `Ability.QueueCastingJob`; a failed read-back is a native failure, not a receipt. Evidence: `AbilityEffect.permit` / `.psycast`. |
| Ignite (`IgniteIntent`) | `Ignite guard <name>: <reason>` (InvalidRequest): `pawn`, `drafted`, `molotov`, `cell`, `room`, `occupied` (any pawn, thrower included, inside the target room), `throw` (the attack_ground order refused: `no_ground_verb`, `cannot_hit`, `not_drafted`, `native_refused`). |
| Research selection (`ResearchIntent`) | Known project, not anomaly knowledge, unfinished and startable now (`CanStartNow`); the current project applies again. Applied is terminal, refused replans. |
| Area (`AreaIntent`) | Operation required; exactly one of home, pollution_clear or a bot area key; home and pollution-clear areas are never created or deleted; delete takes no cells; cell edits need cells in bounds; the map cannot make another allowed area (`CanMakeNewAllowed()` false); bot area does not exist (NotFound). No snapshot token. |

## Trades

Map trades dispatch under a running clock like every routine kind; accept revalidates
each staged thing at apply time. Opening requires a reachable, eligible negotiator: an
adjacent one opens the session at once; otherwise native orders vanilla's TradeWithPawn
job, which follows the trader, and opens the session (never the dialog) where that job
would open `Dialog_Trade`. An interrupted walk is not reissued. Sheet reads, line staging,
accept and cancel need only the session's participants present and tradeable. Native
sessions bind the exact deal, participants, map and colony load.

Every trade operation is an idempotent intent naming the session's trader and negotiator;
accept also carries the preview signature covering exact rows, counts, stock identities
and prices. Native acceptance rechecks stock eligibility, both silver balances and trader
availability. Dispatch does not gate on a preview: native judges the intent against live
state when it applies. A lost receipt sends the intent again.

An accept the game declines to execute (`TryExecute` false) still closes the session but
is refused with `FAILURE_CODE_NATIVE_FAILURE`, never applied; the routine reads it as
settling that caravan for the occurrence and replans. An applied accept's `after_silver`
is the colony silver read from live stacks after the exchange. The routine replans from
live state (`ReadTradeSession` issues no orders). Exchange completion does not certify
storage. A caravan reported still travelling holds the concern open and lends native ticks
until it arrives.

**Favor currency.** A favor session (the Royalty tribute collector) sets `TradeSheet.currency_kind` (absent reads as silver): row `sell_price` is the favor value, the favor row is `TradeLine.favor` (a currency row, never `protected_export`) and its `transfer_count` is the favor granted on accept. Accept needs no `Silver` floor and exempts currency rows from the reserve check but refuses a negotiator without a royalty tracker. Only here are secure, non-downed prisoners (`TradeUtility.AllSellableColonyPawns`) sellable; an extra home/host faction costs goodwill (`MemberSold`). The controller sells only surplus prisoners (`RoundsFacts.SurplusPrisoners`) with a known positive favor price; unknown facts hold. The staged human-race line gets the accept floor of 0.

**Policy trades** select bounded purchases and surplus sales from the fresh native sheet.
`economicFloors` on native acceptance contains exact `Def=count` entries separated by
semicolons, including Silver. Acceptance checks remaining actual stack counts in the same
main-thread operation as the exchange and refuses protected exports. Unknown or truncated
inventory prevents selection. A policy with no eligible affordable lines cancels its own
session without an exchange. Neither cancellation nor acceptance takes a trader quest.
Trade is routine-only (the `trade` family, `--routine-silver-reserve`,
`--routine-item-wealth-share`); there is no player trade command. Sales come from stock
above a MaintainResource target and, once the item share of colony wealth passes
`--routine-item-wealth-share`, from raw-material hoards (steel, plasteel, gold, uranium,
jade) sold down to the highest of the target, the economic floor and a retained minimum
(`policy.WealthSurplus`); an unknown wealth split sells nothing on that rule.

Direct orbital opening is refused. Ordinary orbital input requires the comms console's
native menu, a powered reachable interaction cell and capable negotiator, then
`UseCommsConsole` and the native trade dialog; adjacent map-trade checks cannot authorize it.

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

## Production bills

Auto reviews matching cooking, reserve and animal-butchery bills regardless of ownership.
Known suspension, finite repeat mode, insufficient targets, narrowed ingredient filters
and worker restrictions trigger a guarded replacement through the production action.
Unknown fields remain unknown; adequate bills and unrelated recipes remain. Same-recipe
replacement stays on its original bench and works on a full stack. The stack token
protects the edit; completion still requires observed production.

Native bill progress rejects a changed configuration or stack index even when output was
produced. The unsuccessful detail names up to six changed fields with captured and current
values, an omitted-field count, and a 1536-byte ASCII bound; collection fields and long
or non-ASCII scalars use full SHA-256 digests. These diagnostics are emitted only for
unsuccessful progress, not colony facts, and native pause state stays outside the
configuration hash. Saving can change a bill (`Bill.ExposeData` prunes ingredient
definitions excluded by the recipe's fixed filter); Production/configuration reproduces
this for a Kibble bill.

## Related reading

See [spatial contracts](spatial-contracts.md), [sessions and
recovery](../architecture/sessions-and-recovery.md), and [plans and
Hands](../architecture/plans-and-hands.md).
