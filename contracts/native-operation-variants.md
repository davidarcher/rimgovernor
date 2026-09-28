# Implemented native operation variants

Source audit of the production native tools under
`integrations/rimgovernor-native/src/Bridge`. This page records implementation
branches, selectors, defaults and aliases; it does not establish actual SDK
binder behavior.

All rows are **source-audited, migration acceptance pending**. G01.02 owns the
shared schemas. N01.02 owns the first typed boundary, N01.03 observation migration,
N01.04 gameplay operations, and N01.05 clock/presentation operations, as specified
below; these phase owners identify the subsequent implementation and acceptance work.

Source links refer to the compiled implementation.
Closed selectors below are tool vocabulary. Native definition names, IDs, labels,
UI fields and generated schedule keys remain discovered values, not closed enums.
This is not a field-by-field result schema or a complete numeric-limit audit.

## Operation selectors

Unless a row says otherwise, an unsupported selector returns a refusal/failure;
that does not prescribe one uniform JSON failure shape. `Trim/lower` means
whitespace is trimmed and invariant lower case is used; `exact` means ordinary
case-sensitive equality with no normalization. Defaults are C# entry defaults,
not a claim about SDK omitted/null conversion. N01.04 owns rows in this table
except the observation rows (N01.03) and clock/presentation rows (N01.05).

| Tool | Implemented selector and owning method | Conditional behavior and uncovered acceptance |
| --- | --- | --- |
| `home/recover_service` | [RecoveryTools.cs](../integrations/rimgovernor-native/src/Bridge/RecoveryTools.cs): `Order`. Exact method repair/breakdown/refuel maps to WorkGiver_Repair/WorkGiver_FixBrokenDownBuilding/WorkGiver_Refuel class names; no default method. | dryRun defaults true; refusal has success equal to dryRun and accepted=false. It can construct a candidate native job in preview but does not issue it. Verify actual repair/fuel recovery and shortages independently, including refused preview versus failed execution decoding. |
| `home/relieve_need` | [NeedReliefTool.cs](../integrations/rimgovernor-native/src/Bridge/NeedReliefTool.cs): `Relieve`. Exact need rest/food/joy; no default. Native giver branch selects GetRest/GetFood/Recreation. | dryRun defaults true and stops before generating the job; absent or no-longer-low need refuses. Food accepts an Ingest job only. Verify native eating/rest/recreation, absent needs, player schedule/job changes and no implicit production. |
| `home/population` | [PopulationTool.cs](../integrations/rimgovernor-native/src/Bridge/PopulationTool.cs): `Population`. interaction null reads; supplied text resolves exact native PrisonerInteractionModeDef, then restricts to AttemptRecruit and MaintainOnly objects. | Advertisements say Recruit/MaintainOnly, but accepted strings are the installed objects' defName values; no literal Recruit alias is implemented. Discover `interactions[].name`. Empty interaction is a supplied invalid request. dryRun defaults true. Verify returned definition names, native recruitment eligibility, prior-setting mismatch and actual recruitment separately. |
| `home/upkeep_wall` | [WallUpgradeTool.cs](../integrations/rimgovernor-native/src/Bridge/WallUpgradeTool.cs): `Apply`, `WallUpgradeSafety.Release/Remove`. Exact action release/remove; no default; all others return failure. | dryRun defaults true. release and remove are distinct guard lifecycle operations; source/backup/permanent/material identities describe their admitted construction. Verify ordinary removal/replacement, release, support failure, player changes and load invalidation. |
| `home/player_input` | [PlayerInputTool.cs](../integrations/rimgovernor-native/src/Bridge/PlayerInputTool.cs): `PrivatePlayerInput.Apply`, `Key`, mailbox reader. Exact action take/renew/event/release; event kind move/down/up/wheel/keyDown/keyUp. Mailbox allows event/renew/release only. | No dryRun; owner/frame/order/UI guards precede native injection. Unsupported action/kind/key throws ArgumentException; unavailable native keymap throws InvalidOperationException. Key vocabulary is detailed below. N01.05: ownership acquisition/release, repeated release, stale frame/UI, held-key cleanup, mailbox restrictions and private rendered input on supported platforms. |
| `home/runtime_health` | [RuntimeHealthTool.cs](../integrations/rimgovernor-native/src/Bridge/RuntimeHealthTool.cs): `Read`. No parameters. | Read-only: every required authority hook by name with installed/resolved/error, whether the typed clock hooks are installed (lazily, by the first clock epoch), the clock event journal (`journal.initialized`, `newestCursor`, and `corruptRows[]` of `{cursor, error}`: retained rows whose file no longer decodes, which `clock_read_events` reports as lost cursors), and the current authority snapshot. Never installs or repairs; `NativeAuthorityHooks.Install` retries missing hooks from the next admission or update poll, an unresolved target is restart-required, and a corrupt row stays listed for the life of the process. Accepted by the `lifecycle/runtime-fault` acceptance case (both faults from `RuntimeFaultFixture`: `test/runtime_fault_unpatch`, `test/runtime_fault_corrupt_row`). |
| `home/list_buildings` | [ListBuildingsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListBuildingsTool.cs): `Build`. Trim/lower status all/built/blueprint/frame/pending; null defaults all. Category artificial/all; null defaults artificial. Blank and unknown refuse. | Observation options aggregate/inspect/billIngredients change detail; `byPlayerOnly` overrides playerOnly when supplied, except playerOnly=true/byPlayerOnly=false explicitly refuses. N01.03: category/status combinations, alias precedence and unavailable bill ingredients without mutating jobs. |
| `home/list_things` | [ListThingsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListThingsTool.cs): `Build`. Trim/lower category haulable/food/weapons/all/buildings, null defaults haulable; blank/unknown refuses. ownership is different: only trimmed case-insensitive all disables oursOnly; every other value, including null/blank/unknown, selects ours. | includeHeld and corpses add independent observation branches. N01.03: preserve/document ownership fallback before changing it, category/held parity and unavailable corpse facts. |
| `home/get_cells_plus` | [CellsPlusTool.cs](../integrations/rimgovernor-native/src/Bridge/CellsPlusTool.cs): `ParseRect`, `ParseSelection`, `ParseFieldSpec`. fields: areas/designations/fogged/passable/roof/terrain/things/walkable/zone. thingFields: build/className/defName/forbidden/hitPoints/label/owner/plant/stackCount/stuff. Comma-separated, case-insensitive trimmed tokens; all expands; null/blank selects all; comma-only refuses. defName always included. | summary emits aggregates instead of cells and lifts ordinary cell cap; sparse omits uninteresting cells. Rectangle aliases x0/z0/x1/z1 and minX/minZ/maxX/maxZ join x/z/width/height. Conflicting alias/origin/extent inputs refuse; reversed inclusive corners normalize; omitted extents default to 1. N01.03: every field token, unknown/comma-only lists, rectangle alias conflicts, summary/sparse parity, unavailable values and read-only invariance. |

`PrivatePlayerInput.Key` accepts KeyA–KeyZ, Digit followed by one char.IsDigit character, parsed F1–F12,
and the exact keys Escape, Enter, Space, Backspace, Delete, Tab, ArrowLeft,
ArrowRight, ArrowUp, ArrowDown, ShiftLeft, ShiftRight, ControlLeft, ControlRight,
AltLeft, AltRight, Comma, Period, Minus, Equal, Home, End, PageUp, PageDown,
BracketLeft, BracketRight, Slash, Backslash, Semicolon, Quote and Backquote.
F suffix parsing uses int.TryParse rather than an exact canonical F-name list.
The display keymap must resolve the result; these strings do not guarantee that
an installed display supports the key. Button range and wheel behavior remain
input contract fields, not extra action selectors.

## Field-driven operations

N01.04 owns these operation migrations except placement previews (first vertical
slice N01.02), research's read projection (N01.03), and world presentation (N01.05).

| Tool | Implemented branch and owning method | Unknown/unavailable behavior and uncovered acceptance |
| --- | --- | --- |
| `home/gear_upkeep` | [GearUpkeepTool.cs](../integrations/rimgovernor-native/src/Bridge/GearUpkeepTool.cs): `Run`. target=null and dryRun=true inspects all or selected colonists; target supplied **or** dryRun=false enters exact pawn/target/signature validation. Target resolves weapon first, otherwise apparel; dryRun defaults true. | Missing required target/signature, changed loadout or no eligible improvement fails. Verify weapon/apparel preview and ordinary Equip/Wear, forced/locked outfit preservation, and false-without-target refusal. |
| `home/medical_operations` | [MedicalOperationsTool.cs](../integrations/rimgovernor-native/src/Bridge/MedicalOperationsTool.cs): `Inspect`. Null/empty recipe inspects discovered surgery/part rows; nonempty recipe plus part selects native recipe/body part. dryRun defaults true; real selected operation queues a medical bill. | Unsupported confirmation/postcondition recipes remain inspectable but cannot queue; missing supplies/practitioner, care/health changes or existing bills refuse. false without recipe still reads after its stricter identity check. Verify whole-body versus part recipes, no-effect previews and observed surgical health outcome. |
| `home/placement_previews` | [PlacementPreviewsTool.cs](../integrations/rimgovernor-native/src/Bridge/PlacementPreviewsTool.cs): `Preview` delegates to HomePlaceBuildingTools.Preview. JSON string contains 1..64 rows, exactly defName/x/z/rotation/stuff; rotation shares the parser above, but input string cannot be blank. | Duplicate/unknown/missing keys, wrong scalar types, overflow or blank required strings refuse. No dryRun/write/godMode option; always preview. N01.02: cross-language fixtures, actual SDK adapter invocation, invalid input and unchanged native refusal/no-effect outcomes. |

## Single-operation and observation coverage

These rows explicitly account for the remaining exports; absence of an op string
does not make boolean branches or runtime object variants interchangeable.

| Tool | Owner, implementation and variants | Concrete migration acceptance still uncovered |
| --- | --- | --- |
| `home/acquire_resource` | N01.04; [ResourceAcquisitionTool.cs](../integrations/rimgovernor-native/src/Bridge/ResourceAcquisitionTool.cs), Acquire/Product/Eligible. Exact resource/source/position/session; native mineable versus wild plant/tree selects designator; dryRun=true default. | Unknown resources, changed source, safe native designation and actual harvested/mined stock. |
| `home/cancel_construction` | N01.04; [CancelConstructionTool.cs](../integrations/rimgovernor-native/src/Bridge/CancelConstructionTool.cs), Cancel. Exact blueprint/frame plus expected definition/stuff/position/session; dryRun=true default. | Blueprint/frame refusal and native refund, unsupported installation/finished building, stale identity. |
| `home/confirm_colony_names` | N01.04; [ColonyNamingTool.cs](../integrations/rimgovernor-native/src/Bridge/ColonyNamingTool.cs), Confirm. Exact known bootstrap naming window and suggestions; dryRun=true default. | Stale/other windows, invalid names, unchanged preview and native named objects/dialog closure. |
| `home/manage_waste` | N01.04; [WasteTools.cs](../integrations/rimgovernor-native/src/Bridge/WasteTools.cs), Manage. Exact waste/pawn; unwanted/bury CSV sets select admitted items and burial versus hauling; dryRun=true default. | Protected/named corpses, unavailable destination, actual delivery or burial, no destruction shortcut. |
| `home/recovery_area` | N01.04; [RecoveryTools.cs](../integrations/rimgovernor-native/src/Bridge/RecoveryTools.cs), Area. Exact pawn and observed existing areaId; assigns/renews a 600-tick restriction during toxic fallout; dryRun=true default. No explicit release selector. | Unknown area, player restriction widening, expiry/load restoration and later player override. |
| `home/upkeep_bed` | N01.04; [UpkeepBedTool.cs](../integrations/rimgovernor-native/src/Bridge/UpkeepBedTool.cs), Assign. Exact pawn/bed/previous bed; dryRun=true default. | Stale previous ownership, protected beds, assignment readback and actual sleeping. |
| `home/upkeep_home` | N01.04; [HomeCoverageTool.cs](../integrations/rimgovernor-native/src/Bridge/HomeCoverageTool.cs), Apply. Exact owned target and observed batch shape/revision; dryRun=true default. | Connected interiors, bounded batches, changed shape/revision, autonomous restoration, exact area change and reload. |
| `home/colony_facts` | N01.03; [ColonyFactsTool.cs](../integrations/rimgovernor-native/src/Bridge/ColonyFactsTool.cs), Facts. planning false/true changes optional planning facts. | Both projections, unavailable native definitions/facts, no orders/tick changes. |
| `home/colony_identity` | N01.03/06; [ColonyIdentity.cs](../integrations/rimgovernor-native/src/Bridge/ColonyIdentity.cs), Identity. No selector; read can attach missing identity component/create colony ID. | No-game/map failure; old-save component attachment exactly once, stable colony ID and rotated load token. |
| `home/list_pawns` | N01.03; [ListPawnsTool.cs](../integrations/rimgovernor-native/src/Bridge/ListPawnsTool.cs), ListPawns and PawnFilters. Boolean scope/detail selectors and nameFilter compose; installed pawn/hediff/need/work/animal definitions are dynamic. | Incompatible/empty filter combinations, absent needs/dead/held categories and native read-only invariance. |
| `home/recovery_state` | N01.03; [RecoveryTools.cs](../integrations/rimgovernor-native/src/Bridge/RecoveryTools.cs), State. No selector; building service variants are observations. | Absent versus destroyed building, unavailable power/fuel and native unchanged state. |
| `home/waste_state` | N01.03; [WasteTools.cs](../integrations/rimgovernor-native/src/Bridge/WasteTools.cs), State. unwanted/bury CSV identity sets; source corpse/item classes and containment are dynamic. | Named/protected/burial/rotten/spoiled branches and no mutation from inspection. |
| `home/world_progression` | N01.03; [WorldProgressionTool.cs](../integrations/rimgovernor-native/src/Bridge/WorldProgressionTool.cs), Read/ReadNow. includeStorage false/true; dynamic caravans/assemblies/quests. | Departed/returned/missing caravans and optional storage availability, no invented progress. |
| `home/resource_sources` | N01.03; [ResourceAcquisitionTool.cs](../integrations/rimgovernor-native/src/Bridge/ResourceAcquisitionTool.cs), Sources. Exact dynamic resource; development toggles infrastructure projection. Output method mine/cut/harvest is selected from native source kind, not accepted as an input op. | Unknown definition/no map, no reachable colonist, empty/blocked sources and nonmutating development probes. |
| `home/roof_support` | N01.03; [RoofSupportTool.cs](../integrations/rimgovernor-native/src/Bridge/RoofSupportTool.cs), Read. Exact target only; hypothetical exclusion of this support. | Missing/non-wall target, unavailable native support facts and no roof changes. |
| `home/spatial_access` | N01.03; [SpatialAccessTool.cs](../integrations/rimgovernor-native/src/Bridge/SpatialAccessTool.cs), Inspect. blockedCells/targetCells CSV sets define counterfactual input; no construction op. | Invalid/duplicate/off-map cells, bounded reachable outputs and no path/grid mutation. |
| `home/wall_upgrade_sites` | N01.03; [WallUpgradeTool.cs](../integrations/rimgovernor-native/src/Bridge/WallUpgradeTool.cs), Sites. Exact target; native straight/corner geometry produces candidates. | Unsupported geometry/material/access and no designations on reads. |
| `home/render_demand` | N01.05; [RenderDemandTool.cs](../integrations/rimgovernor-native/src/Bridge/RenderDemandTool.cs), Demand/Lease. Entry seconds=0 default is advertised status; positive values renew render demand. Entry accepts 0..30 and throws outside that range. Lease can initialize driver/install patch even on zero. | Batch unsupported; zero initialization versus pure status claim, out-of-range refusal and lease expiry/visible-window rendering. |

## Compatibility decisions still requiring evidence

The selector lists above were read from source, not inferred solely from SDK
descriptions. Several advertised summaries are narrower than implemented input:

- Bill string booleans accept yes/no/true/false; pawn/building booleans also accept
  1/0. Numeric rotation strings are accepted by place_building. These aliases need
  deliberate schema treatment and fixtures before strict validation replaces them.
- Native enum TryParse in both clock tools accepts numeric text without IsDefined.
  The other clock enum members and numeric overflow need actual runtime probes;
  do not describe advertised names as the complete accepted language.
- list_things ownership unknown values fall back to ours. A future strict refusal
  is a behavioral change, even though it is safer than silent fallback.
- Population's advertised Recruit text must be reconciled with the discovered
  AttemptRecruit object's defName; the source does not implement that alias.
- Field defaults depend on presence: place_building requires explicit dryRun;
  order defaults to execution; trade has no dryRun; most other writes default to
  preview. Shared argument plumbing must preserve these distinctions until a
  coordinated schema change. Unknown argument metadata is not rejection by itself.
- Preview/read labels do not mean no runtime bookkeeping: identity can initialize
  save metadata; render status can initialize its driver. Separate permitted
  bookkeeping from gameplay effects in the acceptance contract.

N01.02/03/04/05 must capture actual discovery and representative replies for
every branch family, including omitted versus null versus blank, case/alias
behavior, unknown keys, refused and unavailable results. Dynamic native names
require installed-definition fixtures, not copied hardcoded lists. Method-source
verification does not establish serialization, binding, pawn completion, stale
queued dispatch, lost-reply recovery or save/load behavior. Those remain open in
[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22); the linked existing
native scenarios are candidates, not evidence produced by this audit.
