# Proposal

## Why

The food pilot converted requirements recovered from agent-written documentation,
implementation and tests into a standing specification before establishing the
maintainer's intended behavior. Replace that baseline with the food policy
confirmed in this chat, and capture the shared production responsibilities it
depends on without claiming the current implementation satisfies them.

## What Changes

- Replace the standing food specification at the maintainer's explicit direction,
  preserving RG-FOOD-001–008 and extending the catalog for eating timing, dietary
  exceptions, scarcity priorities, travel provisioning and dining conditions.
- Cover every colony dependent, nutrition actually used, meal quality, assisted
  feeding, food safety, resilient reserves and preservation across future supply.
- Let the governor choose the balance of food investment and other colony needs.
  Under scarcity, prioritize colonists, then guests, then prisoners and animals;
  consider consequences within those constraints. Dietary and ethical exceptions
  may include any valid game action in emergencies, including cannibalism.
- Capture shared acquisition, hunting, growing and herd responsibilities in
  separate draft capabilities. Their detailed scenarios are review examples,
  not additional decisions attributed to the maintainer.
- Keep source/test inspection and execution evidence separate from intent. The
  expanded requirements receive no new implementation-completeness or pass claim.

## Capabilities

### New Capabilities

- `resource-acquisition`: combine useful outputs and shared costs across colony
  goals; evaluate optional trade with its full transaction and expedition costs.
- `hunting`: choose and complete wildlife acquisition for food or other goals,
  accounting for capability provision, danger, retrieval and useful outputs.
- `growing`: plan crop production for food, feed and other colony resources,
  including justified investment in protected production.
- `herd-management`: choose herd size and feeding strategy according to colony
  goals, net contribution, upkeep and resilience.

These are new OpenSpec capability drafts, not claims that these systems are new
to the product. They extend and reference existing supply and commitment intent;
they do not migrate those entire documentation collections.

### Modified Capabilities

- `food-security`: replace recovered intent with the maintainer-confirmed food
  outcomes and tradeoffs. Five existing requirements are modified and eight are
  added to OpenSpec, including the three previously inferred RG-FOOD IDs.

## Impact

This capture changes requirement documents and linked authority/navigation only.
It implements no planner, game operation or workflow configuration. The standing
food spec is replaced now under explicit authorization; the other four standing
capabilities are not created. Design and implementation tasks are not part of
this capture. The change remains active for their later preparation.

Existing evidence remains in the food, supply and commitments requirement pages.
Future implementation must assess each changed claim through the existing path:
typed native facts, pure policy, shared demand and Method admission, Hands, native
operations and observed outcomes. Equipment management provides hunting capability;
habitat provides kitchen, storage and dining facilities; travel owns journeys;
food planning accounts for the feeding consequences. No parallel demand pipeline
or new state owner is authorized by this specification.

## Decision provenance and review boundary

Source: the maintainer's exploration in this chat on 2026-10-25, ending with
"lets write it up" and "you can replace the existing food spec that was autogen'd
with this confirmed one." The following records decisions, not implementation
evidence. Requirement wording and scenarios are the assistant's formalization.

| Confirmed decision | Maintainer direction |
| --- | --- |
| Adequacy and resilience | Avoid starvation and its downsides; reserves smooth channel losses; multiple channels reduce risk; prepare for significant game-event disruption. |
| Autonomous balance | "bot choose that balance autonomously" |
| Meal quality and nutrition | Weigh extra resources against mood; preparation's increased nutrition matters; useful intake and eating-time waste matter. |
| Dependents | "everyone, not just colonists" |
| Emergency exceptions | Normally respect dietary/ethical restrictions; exceptions require consequence comparison and extend to "all valid game actions, including cannibalism". |
| Scarcity | Colonists first; guests next; prisoners/animals after; weigh consequences and release when needed, infrequently. |
| Preservation | Value a freezer's ongoing benefit; durable reserve foods may also be frozen for resilience. |
| Herds | The bot chooses herd size for its goals and may increase it to feed colonists; grazing and supplied feed are alternatives. |
| Shared acquisition | Hunting can serve leather demand with meat as a secondary benefit; crops serve food and non-food demand. |
| Protected growing | Proactively invest when its production and resilience benefits justify the cost. |
| Additional feeding obligations | After the final gap review: "the other points make sense" confirms access/assistance, safety, loss reduction, anticipating dependents and whole-chain resilience. |
| Away feeding and trade | World travel and quests need support; trade is optional and fully costed, with no requirement for routine food-buying expeditions. |
| Dining | "that principle works" confirms suitable dining subject to access, individual needs, urgency and total colony cost. |

No fixed reserve duration, eating percentage, crop list, weapon type, guaranteed
survival under every event, or fixed food-channel count was approved. Cooking is
normally expected to win on value, not imposed universally. Scarcity priorities
do not define a prisoner-versus-animal ranking. Concrete scenario setups below
illustrate the decisions; they are not existing test results or a new source of
product authority. Explanations are a cross-cutting product responsibility, with
food-specific player information described in the food spec; no reporting system
is designed here.
