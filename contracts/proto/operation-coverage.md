# Operation and effect contracts

`operations.proto` covers ordinary production mutations as closed commands, previews and guarded execution. `receipts.proto` separates pre-admission `common.Failure` from admitted no-change/applied/uncertain outcomes and later correlated progress. Official generated transport messages are not validated domain values. Native adapters must enforce these requirements; this contract slice does not implement them.

## Admission, replay and evidence

Every execution requires exactly one command and a complete `authority.WritePrecondition`: current colony/load/map, positive native generation, live lease and complete controller-session/action/positive uint64 attempt key. Exact target IDs and required snapshot tokens are checked on the game thread with normal native predicates immediately before effects. IDs are opaque nonblank strings, at most256 UTF-8 bytes, no NUL. Map zero is valid; negative map IDs are refused. Tokens bind the entire relevant state to identity and exact entity; they are not caller-asserted hashes. Observation `SnapshotRef` supplies context/entity/token. A stale or unavailable token refuses mutation, never falls back to coordinates, names or weaker checks.

The per-load unsaved attempt ledger holds at most4096 admitted attempts and does not evict. Full capacity refuses new admission before effects. Same key and equal canonical generated-message field values (including presence, repeated order, selected oneofs and original preconditions) replay the original receipt without effects. Changed fields conflict. Unknown fields are rejected. Serialized binary bytes alone are not equality. A retry may retrieve a recorded result after lease expiry, but cannot create a new effect. Identity mismatch never replays across loads. New load loses the ledger; unknown lookup does not authorize retry.

An admitted receipt preserves admission context and authorizing owner/direction even after subsequent revocation. Applied means the concrete effect in evidence was observed, not that pawn work completed. NoChange is a known unchanged postcondition; it is not a catch-all semantic refusal. Any exception/readback failure after effects might have begun produces Uncertain. Multi-field settings and per-cell mutations can partially apply: evidence records per-field/per-cell outcomes, and uncertainty remains when any affected state cannot be read. There is no claimed transaction rollback.

Progress observation is a separate read with complete AttemptKey and current context. Unknown/pending/completed/absent are explicit alternatives. Completed or absent requires complete fresh correlated inspection causally after dispatch, with observed tick greater than or equal to the admitted tick; context must match the original world/load/map, and current generation supplies observation freshness rather than retroactive write permission. Absence requires proving no relevant remaining order or effect, not an empty/truncated page or missing ledger row. Construction attribution preserves origin/current native ID, definition, material, cell, rotation and stage. Matching another building at the same coordinate is insufficient. The Go orchestrator owns prepared/dispatched/cancelled stages and durable uncertainty, not native receipt DTOs.

## Bounds and presence

`ReleaseOwnedDraft` is a narrow cleanup capability valid after Manual or lease
expiry. It checks exact identity, original controller/direction, native draft
claim and unchanged pawn snapshot on the game thread. It can only release that
claim, never acquire draft, replace a job or clear a player's changed draft. It
uses a separate correlated release result and typed job evidence including claim
ID. A full ordinary attempt ledger cannot prevent cleanup. Released, same-claim
already-released and uncertain are distinct; unknown or changed ownership refuses
without effects. Native per-pawn claim state retains its latest release identity
until replaced, so a retry never targets a new claim. Cleanup results retain the
original owner and actual observed generation without reviving the old lease.

Trade `executed` records native TryExecute success and `actually_traded` its
separate out-result. Transfer acceptance requires both explicitly true; staged
line counts or a closed dialog do not prove transferred assets. Native exceptions
after possible transfer are uncertain even if the existing SDK reports false.
Progress has an explicit unsuccessful outcome for fully inspected failure,
cancellation/interruption, expired quest, dead target or unmet postcondition.
Missing surgery bills alone establish neither completion nor failure; inspect
the exact patient/recipe/body-part outcome. Completed, absent and unsuccessful
outcomes require causally fresh complete inspection; a current Go consumer's
strictly-later-tick rule must adapt to same-tick synchronous verified readbacks.

Production policy requires all four replacement wrappers (floors, commitments,
stopped definitions, drills); present-empty explicitly clears the respective
policy. An omitted wrapper refuses rather than clearing unreadable state. Settings
results for WORK/TRAINING/SCHEDULE identify the exact work definition, trainable
definition or hour (0–23). Multiple entry outcomes must not collapse into an
ambiguous field-category result. Cascaded native changes appear in the complete
readback snapshot; unreadable entry outcomes remain UNKNOWN.

- One operation per attempt. Cell selections and per-cell output: at most4096 expanded unique cells, including rectangle area; overflow refused before work. Repeated non-cell mutation rows at most256 each, with combined filter selectors at most256. Schedule exactly24 assignment definitions. Duplicates refused where they would make a target/definition ambiguous. No silent truncation of requested effects.
- Definition names remain open valid identifiers, subject to the shared limit of 256 UTF-8 bytes and no NUL. Diagnostic prose is at most4096 Unicode scalar values, shortened only at scalar boundaries. Opaque IDs/tokens are at most256 UTF-8 bytes. Native int32 counts nonnegative except signed trade transfer amounts; cargo quantities positive. All floating values finite; native policy ranges still apply. Skill0..20, work priority0..4, rotation uses the shared placement.Rotation enum in requests and evidence, requiring a single cardinal value for writes and observed effects (neither ALL nor UNSPECIFIED). Whole-body surgery has absent part_index, never an invented negative body-part identity.
- Every required message, enum, optional scalar and oneof must be present and valid at admission. Unspecified/unknown enum values and unknown message fields are refused recursively. Presence of false/zero is meaningful. Assignment.clear differs from absent patch. Bill filter replacement present-empty denies all; replacement cannot be combined with allow/disallow. Special-filter selectors are zone-only; bills accept definition/category selectors supported by fixed ingredient rules.
- Tokens replace whole before/after snapshots in narrow receipts. Readbacks use separately bounded observation pages; tokens must be resolvable to the exact scoped observation and may not stand in for missing facts. Preview projected evidence has no fabricated native job/bill/blueprint IDs and cannot be used as an observed after token. Complete placement limits remain16 candidates/4 rotations/4096 cells/blockers/256 costs and1MiB reply. General reply transport limit is root-owned; failing to represent full receipt evidence after effects means uncertain, not silent truncation.

## Production coverage

Source files below are under `integrations/rimgovernor-native/src/Bridge`. Read-only/mixed read branches belong the observation schema; user UI and clock/lifecycle belong their separate schemas. Source signatures and consumer evidence were audited against `110a1391` plus the independent authority checkpoint; no legacy JSON parity is required.

| Current production capability | Closed commands | Required effect/reconciliation |
|---|---|---|
| PlaceBuildingTool.cs home/place_building | PlaceBuilding | Ordinary blueprint, exact lineage; completed construction observed separately. Shared placement preview retains costs/site facts. |
| CancelConstructionTool.cs | CancelConstruction | Exact original target, def/stuff/cell, cancellation and affected frame/material observations; never cancel replacement. |
| InstallTool.cs home/install | InstallBuilding | Stable inner ID, packed/queued/installed state; WipeExistingThings then blueprint placement can yield uncertain partial effects. Status is a read. |
| ResourceAcquisitionTool.cs | AcquireResource | Exact product/source/cell, ordinary plant/mining designation, later actual output. Current unresolved mining ledger max256. |
| ExcavationTool.cs | ExcavateCell | Exact cell and rock def, ordinary Mine designation (adopts an existing one idempotently), single-cell counterfactual roof support and a separate per-pick guard that never relaxes MiningBlocker. Completion requires the cell actually cleared; yield is never evidence. |
| Upstream apply_architect_designator | DesignateThing allow/forbid/hunt/harvest/deconstruct | Exact inspected thing with native CanDesignate; no generic designator-string dispatch. Cell/roof variants are not silently invented. |
| BuildingConfigTool.cs | PatchBuilding | Forbidden,power,temperature,medical,owner,prisoner flags; field result + exact before/after snapshot. Gizmos are reads. |
| PawnConfigTool.cs | PatchPawn | Work,schedule,care,hostility,self-tend,follow,area,master,training/slaughter/release; cascaded changes in snapshot. Drop/nickname excluded from automation. |
| BillsTool.cs | AddBill/PatchBill/DeleteBill/MoveBill | Native bill ID plus expected bench stack; optional repeat/filter/worker/store fields; exact stack readback. Index alone is not identity. Production is later observation. |
| ResearchTool.cs | SelectResearch | Native eligible project selection, never finish research; current/progress/capability reads separate. |
| ProductionPolicyTool.cs | SetProductionPolicy | Typed floors/commitments/stops/drill rows, owned native enforcement and interrupted pawn IDs. No adopting player drills. |
| ZoneCellsTool.cs | CreateZone/DeleteZone/EditZoneCells/RepairZone/PatchStockpile/PatchGrowing | Exact cells, crop/sow/cut/filter/priority; per-cell refusal and complete geometry/filter readback. Repair and allow_split require explicit reviewed player maintenance. Typed native: `NativeZoneCreation.cs` (any label, any priority/preset/`FilterPatch` body for stockpiles; growing zones stay the fixed crop shape), `NativeStockpilePatch.cs` (priority and/or filter patch on one stockpile under its ListZones token; absent parts preserve the live setting, an unresolved selector refuses the whole body) and `NativeZoneCellEdit.cs`/`NativeZoneDeletion.cs`. `FilterPatch` selectors are exact defNames; `hit_points_min/max` are fractions in [0, 1] and `quality_min/max` QualityCategory names, each pair required together. ListZones `include_filter` returns the live allowed defs, configurable special rules and both ranges; the per-zone token covers them. `zoneaccept` covers all four. RepairZone/PatchGrowing have no typed native handler. |
| HomeCoverageTool.cs | ExtendHome | Shape/revision-bound missing cells only, preserve player exclusions. Coverage is not cleaned floor. |
| UpkeepBedTool.cs | AssignBed | Exact prior assignment + native bed eligibility; assignment differs from sleeping. |
| WallUpgradeTool.cs | RemoveWall/ReleaseWallRemovals | Native handler `NativeWallRemovalOperations.cs` resolves the site from the wall identity through the ListWallUpgradeSites census (straight original with completed same-stuff stone backups, backup of a standing stone permanent wall, or corner with covered materials; several admissible sites refuse), then the legacy continuing guard and ledger512 apply. Completion requires the guarded demolition event; `wallremovalaccept` covers it. |
| RecoveryTools.cs | RecoveryArea/RecoverService | Fallout-safe restrictive area claim/expiry; ordinary repair/breakdown/refuel workgiver. Observe real area ownership/service recovery. |
| WasteTools.cs | ManageWaste | Safe exact-item hauling/burial; preserve protected items/corpses/graves, avoid merged-stack false attribution. |
| NeedReliefTool.cs | RelieveNeed | Expected job or idle + schedule; preview admission only; ordinary AI job and later need recovery. |
| GearUpkeepTool.cs | ImproveGear | Expected loadout, ordinary equip/wear job; observe actual equipped/apparel ID. |
| MedicalOperationsTool.cs | QueueSurgery | Explicit current player intent required; recipe/part/health/care and native materials/worker checks; bill is not surgery success. |
| HusbandryTool.cs | SetAnimalTraining/SlaughterAnimal | Settings+census tokens; recursive training or eligible slaughter designation; later animal outcome. |
| PopulationTool.cs | SetPrisonerInteraction | AttemptRecruit/MaintainOnly/ReduceResistance/Release, plus Enslave/Convert with Ideology; no instant recruitment. Progress reads actual custody outcome (`PrisonerEffect.outcome`). |
| OrderTool.cs | SetDrafted/MovePawn/AttackTarget/PawnTargetOrder | Equip/rescue/capture/tend/haul/work/repair/clean, native job readback; exact owned draft cleanup, preserve player orders. Resolve is observation. |
| TradeTool.cs | OpenTrade/SetTradeLines/AcceptTrade/EndTrade | Native adjacent session/deal signature, absolute signed line counts, economic floors; asset/session/quest readback. Pawn trade requires explicit consent. Sheet/status/list/preview reads separate. |
| CaravanTool.cs | FormCaravan/TravelCaravan | Native assembly/load/route; catalog token binds groups; formation is not departure, return is not unloaded cargo. |
| CaravanGiftTool.cs | GiftCaravanSilver | Exact membership/settlement/faction, native gift execution; before/after silver/goodwill, no direct goodwill edit. |
| QuestTool.cs/QuestFulfillmentTool.cs | AcceptQuest/FulfillQuest | Native choice/accepter/request/confirmation; state transition does not fabricate quest completion/rewards. |
| ColonyNamingTool.cs (`NativeColonyNamingOperations`) | ConfirmColonyNames | Colony-wide, not entity-scoped: exact observed window_id/faction_name/settlement_name stand in for EntityPrecondition/snapshot token, refusing on any drift. Reuses the legacy tool's own dialog lookup, field reflection and native name validators/callbacks. Autopilot-eligible via the ordinary authority `WritePrecondition`; independent of `PlayerPresentation.Apply`'s own confirm_colony_names branch, which stays explicit-player-only (see presentation-coverage.md). |

Controller anchors: `controller/rimgovernor/bridge_game.py` READS/WRITES and guarded invocation; `bridge_runtime.py:987-1138` draft/bill/job validation and post-write readbacks; `hands.py` execution and zone/trade verification; `colony_skills.py:246,370` Unforbid/Hunt; `gear_upkeep.py`, `surgery.py`, `world_progression.py`, `waste_management.py`, `service_recovery.py`, `mood_control.py`. Go requirements distinguish effect tags in `go/internal/domain/progress.go` and require native attribution in construction lineage.

## Admission details requiring native implementation

Fresh tokens must encompass the actual native preconditions, not merely the fields changed: bill stack ordering, pawn job/draft/player-order state, bed assignment, zone geometry/filters, medical health/care, animal census/settings, caravan membership/catalog, trade deal and wall support. RemoveWall's token resolves an exact previously observed complete site, including original/left/right/backup/permanent/material and old/new cells; the adapter must refuse if it cannot resolve that site, never manufacture missing support evidence.

Trade line counts are absolute: positive buys and negative sells in normal trade; native gift mode has its own positive-gift convention. Silver is derived, not editable. Open is adjacent-only. Relative string updates and fuzzy matching are deliberately replaced by exact lines. SetTradeLines.allow_pawns and SetDrafted.allow_persistent_draft require explicit current player intent; they do not grant it. Surgery and destructive zone repair require the same external authorization policy. The executor derives capability policy from authenticated player direction outside model execution, scoped to the exact intended action and targets, and refuses absent/stale authorization. There is no force/confirm boolean that substitutes for it. Normal orders retain native reachability, work settings, reservation, resources, health and player-override checks. Training can cascade, slaughter/release are mutually constrained native designations, and recipe fixed ingredient filters constrain bill settings.

## Other owners and hard source gaps

`home/dialog_text`, player_input, render_demand/video_stream/pawn_image, upstream camera/selection/letter/UI interactions are packaging's explicit player/presentation contracts. `home/confirm_colony_names` is split: its autopilot-eligible path is `Operations.ConfirmColonyNames` above; `PlayerPresentation.Apply`'s own confirm_colony_names branch is a distinct, explicit-player-only path that packaging owns. `supervised_play`, `play_until_event`, save/load/start/stop/time speed are root's clock/lifecycle. All55 owned production exports are partitioned by the source inventory; no fixture exports, god mode, debug acceleration, instant research/health/inventory mutation or arbitrary SDK/gizmo/tool execution enters these operation variants.

Hard native work remains: token producers/resolution/CAS are not implemented for every family; existing native outputs sometimes expose sampled/incomplete filters and reflection-dependent reads. Upstream SDK architect/UI implementations need verified discovery before adding additional variants. Exact cardinal rotation conversion and native whole-body surgery mapping must be tested. Native admitted-attempt ledger and progress attribution are not implemented by these DTOs. Required preview preparation tokens and readback facts must be produced truthfully or explicitly unavailable. These are adapter/gameplay acceptance gaps, not permission for arbitrary payload fallback. Contract compilation validates syntax and official generated C# compatibility only.
