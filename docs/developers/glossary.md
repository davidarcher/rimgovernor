# Terms used in the internals

[Documentation](../README.md) · Reference

| Term | Meaning in RimGovernor |
| --- | --- |
| Colony identity | Native identity used to scope persistent concerns, policies and history. |
| Load token | Identity of the current loaded game context; old in-flight work cannot carry it into another load. |
| Player direction | The intent supplied by the player. There is one author of orders, so no direction counter or compare-and-swap exists; authority is the load token, native tick, native order generation and a pause flag. |
| Run / Hold | The two states of the clock while the bot is on. Running advances at the brain's target speed; Held is paused for one named reason and ends only when the brain sends `Run`. See [Execute under supervision](architecture/control-loop.md#execute-under-supervision). |
| Heartbeat | The brain driver's explicit once-a-second contact with native. If it stops, the one ceiling lowers the tick rate to Normal; it is not a hold. See [the one ceiling](architecture/control-loop.md#the-one-ceiling-and-the-heartbeat). |
| Ceiling | Native's single clamp on the tick rate: the lower of a frame-budget input and a heartbeat input. The brain only asks for a speed. |
| Need | A colony requirement derived fresh each review and never stored: id `family/kind[/subject]`, evidence, satisfaction predicate (met, unmet, unknown), deficit, tier and optional preconditions. Replaces Concern rows, Rounds assessments and Standard episodes family by family. See [Need](architecture/control-loop.md#need). |
| Family | One module per domain that derives its Needs from facts and proposes candidate Methods for them; one scheduler entry. See [Family](architecture/control-loop.md#family). |
| Tier | A Need's urgency class: `SeriousHarm` (also run by the immediate pass), `Wellbeing` or `Development`. Selection orders by tier first. |
| Precondition | A Need's wait on another Need's predicate; replaces phase latches. Explain shows the Need as waiting. |
| Claim | What a Method holds (pawns, cells and footprints, things, zones and sources, materials), derived from its actions by `WorkClaimsOf`. Need-owned Methods are refused only on overlap (`ClaimConflict`). See [claim admission](architecture/control-loop.md#method-journal-claim-admission). |
| Concern | Legacy: a desired outcome, often maintained over time, such as sufficient food supply. Being replaced by Needs. |
| Standard | Legacy: a concern holding a measured target over time; a chore is a Standard whose target is no outstanding work, and a Once Standard settles a finite target and stays settled. Its Episodes, Settle and Once states go away with the [target model](architecture/control-loop.md#target-model-needs-families-and-claimed-methods). See [Concerns and their forms](architecture/control-loop.md#concerns-and-their-forms). |
| Incident | A Concern triggered by an event, one row per occurrence. Its Situation is Active, Clear or Unclear. |
| Safeguard | An admission veto; it rejects proposals and pursues nothing. |
| Department | The colony area a concern serves (Food, Shelter, Industry, Military, Medical, People, Storage, Sanitation, plus a hidden System for game plumbing), like a Civ advisor. A grouping tag only; it never ranks concerns or budgets labor. See [Concerns and their forms](architecture/control-loop.md#concerns-and-their-forms). |
| Capability | A coarse function a planned room can host (sleep, craft, research, cook, store, heat), derived from the facility catalog (`policy.CapabilitiesOf`) and never persisted. The shelter hosts sleep, craft, research, cook and heat, and retires once each has a better home. See [room capabilities](architecture/facilities.md#room-capabilities). |
| Method | A selected way to pursue a concern, retaining its attempts and step associations. |
| Candidate Method alternative | A temporary policy proposal evaluated before journal admission; it has no independent lifecycle or persisted ownership. |
| ColonyForecast | Pure forecast accounting and comparison used to evaluate proposed work. It supplies evidence for Method selection, not admission or execution ownership. See [scope](architecture/colony-forecast.md). |
| Binding | Resolving a selected forecast alternative into exact action targets and quantities; it does not choose another winner or issue orders. |
| Step / action | An accepted unit of work with a stable identity, specification and execution progress. Exact completion depends on its action contract. |
| ColonyPlan | The shared persistent concern/action system used by player requests and routine control. |
| Hands | The deterministic executor that validates and issues accepted native work and retains execution evidence. |
| Admission | Validation before accepting a proposal into the shared plan. |
| Dispatch | The guarded attempt to issue a prepared operation to the native game. |
| Native receipt | A tool response describing an order or operation; not automatically proof of subsequent pawn labor. |
| Postcondition | The observed state required to consider a tracked action complete. |
| Uncertain write | An operation whose native effect is not confirmed; observation is required before considering further action. |
| Acceptance checkpoint | A retained test bundle containing game and harness state for resuming a case; not a requirement for ordinary player saves. |
| Fixture | A declared test input or scenario setup. A native-shaped JSON fixture is not a live game observation. |
| Host | `integrations/rimgovernor-host`: the vendored fork of pardeike/RimBridgeServer (assembly `RimGovernor.Host`), providing tool registration and main-thread dispatch for the mod; its transport is gone, the brain runs in-process (see [brain host](architecture/brain-host.md)). Provenance in `integrations/rimgovernor-native/Notices/host`. May fold into the mod (#3141). |
| Adequately stored (food) | A perishable stock observed sitting in a covered stockpile or an enclosed/cold room, as opposed to exposed to ordinary ambient rot. |
| Store | A zone a department declares (`policy.Store`: role, planned room, filter, priority, further room): one clean zone over its whole room, sized once, deleted only when its purpose is gone. Applied by the `admin/stockpiles` Need; see [storage](architecture/storage.md). |
| Warehouse / yard | The Storage department's two stores: one Low-priority zone over a planned storage room (`indoor_only` filter, no burnable) and one over a planned yard Outdoor room (`outdoor_safe_waste` filter: the items safe outside plus rotten things and waste). |
| Spoilage buffer | The margin the `food-stores/storage` Need keeps: perishable nutrition already stored, at or above the target share (default 9/10) of total at-risk perishable nutrition on hand. |
| Room / PlannedRoom | A Room is the game's own census room (`Room`, `RoomObservation`). A PlannedRoom is one room of the layout plan (`LayoutPlan.Rooms`), carrying a `PlannedRole`: the plan's intent, never an observation. |
| Reconciler | The per-cell diff of a PlannedRoom's wanted ring, floor and furniture against the ground (`policy.Reconcile`, `ReconcileRoom`). Furniture off its slot is packed and the slot filled from packed stock or built on site; standing rooms are reconciled too. There is no separate tidy pass. |

See [plans and Hands](architecture/plans-and-hands.md) for these terms in context.
