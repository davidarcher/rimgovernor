# Terms used in the internals

[Documentation](../README.md) · Reference

| Term | Meaning in RimGovernor |
| --- | --- |
| Colony identity | Native identity used to scope persistent goals, policies and history. |
| Load token | Identity of the current loaded game context; old in-flight work cannot carry it into another load. |
| Player direction | The current authorization and intent supplied by the player; its revision is distinct from ordinary reviews. |
| Goal | A desired outcome, often maintained over time, such as sufficient food supply. |
| Method | A selected way to pursue a goal, retaining its attempts and step associations. |
| Step / action | An accepted unit of work with a stable identity, specification and execution progress. Exact completion depends on its action contract. |
| ColonyPlan | The shared persistent goal/action system used by player requests and routine control. |
| Hands | The deterministic executor that validates and issues accepted native work and retains execution evidence. |
| Admission | Validation before accepting a proposal into the shared plan. |
| Dispatch | The guarded attempt to issue a prepared operation to the native game. |
| Native receipt | A tool response describing an order or operation; not automatically proof of subsequent pawn labor. |
| Postcondition | The observed state required to consider a tracked action complete. |
| Uncertain write | An operation whose native effect is not confirmed; observation is required before considering further action. |
| Clock lease | Renewable permission for supervised simulation, enforced by native code independently of the next Python review. |
| Paired checkpoint | A native save, controller database backup and hash manifest kept together as a resume boundary. |
| Fixture | A declared test input or scenario setup. A native-shaped Python fixture is not a live game observation. |
| GABS | The process used to discover and call the installed game bridge tools. |
| RimBridgeServer | The native bridge providing general game and UI tools; the colony companion extends its capabilities. |
| Outpost | The dashboard's display name; repository and package names remain RimGovernor. |
| Adequately stored (food) | A perishable stock observed sitting in a covered stockpile or an enclosed/cold room, as opposed to exposed to ordinary ambient rot. |
| Spoilage buffer | The margin `MaintainFoodStorage` tries to keep positive: perishable nutrition already stored, above the configured minimum share of total perishable nutrition on hand. |

See [plans and Hands](architecture/plans-and-hands.md) for these terms in context.
