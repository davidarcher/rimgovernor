# Terms used in the internals

[Documentation](../README.md) · Reference

| Term | Meaning in RimGovernor |
| --- | --- |
| Colony identity | Native identity used to scope persistent concerns, policies and history. |
| Load token | Identity of the current loaded game context; old in-flight work cannot carry it into another load. |
| Player direction | The intent supplied by the player. There is one author of orders, so no direction counter or compare-and-swap exists; authority is the load token, native tick, native order generation and a pause flag. |
| Concern | A desired outcome, often maintained over time, such as sufficient food supply. |
| Standard | A concern holding a measured target over time; a chore is a Standard whose target is no outstanding work. See [Concerns and their forms](architecture/control-loop.md#concerns-and-their-forms). |
| Project | A concern with a finite finished state and dependency links to other Projects. |
| Incident | A Concern triggered by an event, one row per occurrence. Its Situation is Active, Clear or Unclear. |
| Safeguard | An admission veto; it rejects proposals and pursues nothing. |
| Department | The colony area a concern serves (Food, Shelter, Industry, Military, Medical, People, Storage, Sanitation, plus a hidden System for game plumbing), like a Civ advisor. A grouping tag only; it never ranks concerns or budgets labor. See [Concerns and their forms](architecture/control-loop.md#concerns-and-their-forms). |
| Method | A selected way to pursue a concern, retaining its attempts and step associations. |
| Step / action | An accepted unit of work with a stable identity, specification and execution progress. Exact completion depends on its action contract. |
| ColonyPlan | The shared persistent concern/action system used by player requests and routine control. |
| Hands | The deterministic executor that validates and issues accepted native work and retains execution evidence. |
| Admission | Validation before accepting a proposal into the shared plan. |
| Dispatch | The guarded attempt to issue a prepared operation to the native game. |
| Native receipt | A tool response describing an order or operation; not automatically proof of subsequent pawn labor. |
| Postcondition | The observed state required to consider a tracked action complete. |
| Uncertain write | An operation whose native effect is not confirmed; observation is required before considering further action. |
| Clock lease | Renewable permission for supervised simulation, enforced by native code independently of the next controller review. |
| Acceptance checkpoint | A retained test bundle containing game and harness state for resuming a case; not a requirement for ordinary player saves. |
| Fixture | A declared test input or scenario setup. A native-shaped JSON fixture is not a live game observation. |
| GABP | The wire protocol the controller speaks directly to the host inside the game (tool discovery and calls). |
| GABP host | `integrations/rimgovernor-host`: the vendored fork of pardeike/RimBridgeServer and pardeike/Lib.GAB (assembly `RimGovernor.Host`), providing transport, discovery and game lifecycle tools; the colony companion extends its capabilities. Provenance in `integrations/rimgovernor-native/Notices/host`. |
| Adequately stored (food) | A perishable stock observed sitting in a covered stockpile or an enclosed/cold room, as opposed to exposed to ordinary ambient rot. |
| Store | A zone a department declares (`policy.Store`: role, planned room, filter, priority, further room): one clean zone over its whole room, sized once, deleted only when its purpose is gone. Applied by `MaintainStockpiles`; see [storage](architecture/storage.md). |
| Warehouse / yard | The Storage department's two stores: one Low-priority zone over a planned storage room (`indoor_only` filter, no burnable) and one over a planned yard Outdoor room (`outdoor_safe` filter). |
| Spoilage buffer | The margin `MaintainFoodStorage` tries to keep positive: perishable nutrition already stored, above the configured minimum share of total perishable nutrition on hand. |
| Room / PlannedRoom | A Room is the game's own census room (`Room`, `RoomObservation`). A PlannedRoom is one room of the layout plan (`LayoutPlan.Rooms`), carrying a `PlannedRole`: the plan's intent, never an observation. |
| Reconciler | The per-cell diff of a PlannedRoom's wanted ring, floor and furniture against the ground (`policy.Reconcile`, `ReconcileRoom`). Furniture off its slot is packed and the slot filled from packed stock or built on site; standing rooms are reconciled too. There is no separate tidy pass. |

See [plans and Hands](architecture/plans-and-hands.md) for these terms in context.
