# Player API direction

This checkpoint is an interim prototype, not the target architecture. The user wants AI decisions to emerge from a comprehensive, discoverable interface to normal RimWorld player information and controls.

RimWorld already owns the simulation. Read its existing Pawn, needs/thoughts/relations, health, equipment, Room, Area/Zone, BillStack, research, animal and job objects. Use the game's existing player-order paths. Do not recreate mood calculations, social simulation, work scheduling, room semantics or game rules in a second engine.

Persist AI intent (strategy, goals, project notes), plus explicit player preferences. Prefer game data for progress/completion. Separate observations from decisions: an enclosure query may report gaps and roofs, but a hard-coded shelter policy should not decide what colony to build. Existing shelter templates, scoring and radius limits are experimental prototype decisions to reconsider, not the blueprint for future tools.

## Coverage audit to complete next

| Domain | Current prototype | Important gaps |
| --- | --- | --- |
| Pawns | Location, equipment, skills, priorities; a few aggregate health counters | Needs, mood/thoughts, traits, relationships, schedules, restrictions, injuries/treatment, ideology context, job blockers |
| Animals | Wild-animal listing and hunting designation | Tame animals, training, bonds, handling, pens, nutrition, medical care, breeding/productive state |
| Rooms/buildings | Placement, limited workstations/bills, roof orders; shelter shortcuts | Actual room assessment, connected systems, fuel/power/temperature, interaction cells, storage and production constraints |
| Architect/orders | Some construction, growing, roofing, allow, limited mining | Complete ordinary order/designation coverage and discoverable available definitions |
| Colony management | Some work, research and production controls | Schedules, policies, restrictions, medical/prison management, trading and other normal player controls |
| Strategic state | Saved horizons/projects with limited metric checks | Rich completion evidence derived from existing game systems; avoid treating declarations as functioning infrastructure |

Expose stable IDs, filters, pagination and targeted detail. Discover definitions and available actions from the game where possible. Keep common summaries concise and load detailed schemas/state on demand; comprehensive coverage must not mean sending the whole map and every tool description on every request.

Do not expose unrestricted reflection, debug spawning, difficulty overrides or arbitrary engine writes. Faithful player access means ordinary player-visible observations and validated player operations, preserving game rules and leaving simulation/execution to RimWorld.
