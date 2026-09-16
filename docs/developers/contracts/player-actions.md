# Player action coverage

Player chat commits semantic requests through the shared ColonyPlan and Hands.
Native settings readback establishes settings, not subsequent sowing, hauling,
production, training or treatment. Native eligibility remains authoritative.

## Native capability audit

The installed bridge exposes the following contracts. Discovery tooling that
records tested DLL hashes and schemas is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).

| Surface | Native contract and execution boundary |
| --- | --- |
| Contextual orders | `open_context_menu` injects a live map click and can act even when no menu remains. It accepts keyboard modifiers. `execute_context_menu_option` accepts an index or exact/partial label, with no expected menu-session token. These tools remain outside model execution. The typed `home/order` path handles supported ordinary pawn jobs. |
| Gizmos | `list_selected_gizmos` reports selection fingerprints, owner IDs, disabled state, grouping and reverse-designator flags. `execute_gizmo` accepts a selection-scoped ID. Inspection is exposed; generic execution remains outside model execution. Explicit draft commands use `home/order`. |
| Dropdowns | No separately named dropdown tool exists. Gizmo execution accepts only its ID; it has no dropdown-option or right-click parameter. Rendered `get_ui_layout` and guarded UI targets can inspect/control supported drawn elements. A menu opening is not an option selection. |
| Reverse designators | Identified in selected gizmo rows; they must not be treated as ordinary immediate toggles. Architect designators have their own discovered placement contracts. No arbitrary reverse-designator fallback is admitted. |
| Queued jobs | `home/order` has no explicit queue parameter. Live clicks accept Shift, but contextual option execution supplies neither a queue identity nor a queue postcondition. Current-job readback does not establish queued-job completion. |

Captured UI targets retain the current load, player-direction revision and exact
selection identities. Dispatch rereads selection and refuses changed or incomplete
selection. Other writes invalidate cached UI targets; a click/scroll consumes its
capture even on uncertainty. After a click, both native UI state and selection are
read again. Native target validation still owns control lifetime and click legality;
Controller checks do not make external player input atomic.

## Supported zone and bill requests

`EditZone` takes an observed numeric zone ID and one operation: add explicit cells,
remove explicit cells, delete, set crop, or edit a stockpile filter/priority. Native
preview rejects unavailable crops, invalid cells and unknown filters before plan
admission. Shared execution rechecks native eligibility and compares fresh zone
geometry, crop or filter with the actual native edit. Deletion removes a designation,
not stored items. Partial or uncertain writes require inspection before retrying.

Storage filters accept existing presets, categories and definitions. The native
filter summary additionally lists configurable `SpecialThingFilterDef` names,
labels, current flags and exact `special:DEFNAME` argument values. Allow/disallow
uses those exact names and native `ThingFilter.SetAllow`. Unknown and nonconfigurable
specials refuse the entire call. Special-only changes affect `changed`; previews
operate on an unattached filter copy. Quality and hit-point ranges are separate
native controls, not special-definition flags.

`CreateBill.ingredients` optionally supplies the complete ingredient whitelist.
Omission preserves recipe defaults. Admission validates the native recipe/filter
preview, including semantic refusals inside successful transport replies. Execution
uses native `only` and verifies the exact bill identity and filter through a fresh
bill read. Recipe ingredient rules and production resource policies still apply.

## Domain boundaries

| Domain | Existing native support | Remaining extension |
| --- | --- | --- |
| Animals | Allowed area, trained master, following, recursive training requests and slaughter designation with native eligibility; animal/training reads. Maintained population, breeder reserve, training and feed targets use [husbandry contracts](husbandry-contracts.md). | Individual master/area/following chat commands; tame/release/sterilize/pen and explicit pair-separation workflows. |
| Medical | Care category, self-tend, medical beds, native tend/rescue orders and patient outcome predicates. | Surgery bills, operation-body-part eligibility and completed operations. |
| Prisoners | Eligible prisoner-bed configuration and pawn/health inspection. | Capture, prisoner interaction/recruitment settings and observed prisoner outcomes. |
| Food/drug/apparel policies | Bill ingredient filters and stockpile filters; native pawn settings inspection. | Typed policy creation/edit/assignment and readback, including restriction versus actual consumption/wearing. |

These extensions and contextual/queued execution acceptance remain tracked in
[Contextual and queued action extensions](https://github.com/davidarcher/rimgovernor/issues/16).
Their absence is explicit; no debug/editor operation or
unvalidated UI fallback substitutes for them.
