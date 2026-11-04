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
  separate capabilities. Their direction was confirmed by the maintainer on
  2026-11-03 (see the second decision table below); their detailed scenarios
  remain review examples, not separately approved tactics.
- Keep source/test inspection and execution evidence separate from intent. The
  expanded requirements receive no new implementation-completeness or pass claim.

## Capabilities

### New Capabilities

- `resource-acquisition`: combine useful outputs and shared costs across colony
  goals in one decomposable comparison; evaluate optional trade with reachable
  traders at its full cost, with no dedicated food-buying expeditions.
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
capabilities were created after their 2026-11-03 confirmation. Design and
implementation tasks are not part of this capture; the change was archived on
2026-11-03 after synchronization, and implementation changes trace to its RG IDs.

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

## Decision provenance for acquisition, growing, hunting and herds

Source: the maintainer's confirmation review in this chat on 2026-11-03, walking
RG-ACQUIRE-001–002, RG-GROW-001–002, RG-HUNT-001–002 and RG-HERD-001–002 one
capability at a time. Each row records the maintainer's direction and the words
that gave it; requirement wording and scenarios remain the assistant's
formalization. Where a tradeoff was discussed, the maintainer chose between
options the assistant laid out, and the chosen option is recorded, not the
discussion.

| Confirmed decision | Maintainer direction |
| --- | --- |
| Acquisition model | One shared acquisition comparison across all demands rather than per-demand ranking with collision coordination ("yes"). |
| Decomposability | The shared comparison attributes a chosen source's value per demand; a scenario under RG-ACQUIRE-001 ("yes"). |
| Over-commitment | Hedging is allowed and explicit: commit more capacity than the point estimate when expected loss justifies it, each resource promised to one use. Double-booking one resource to two incompatible plans stays forbidden. Conditional commitment is a design option, not a requirement ("your rec"). |
| Food-buying expeditions | Dropped. Trade only with traders already reachable at home or on a journey undertaken for another purpose ("food buying expeditions I don't think make sense, lets drop it"). |
| Selling reserves | Weighed, not off the table ("if a trader has something we really need and we had to dip into selling reserves to afford it, that makes sense"). |
| Crop choice | Free choice among everything currently growable, by contribution; no fixed or reserved crop list ("free choice is the mindset. whatever is actually growable"). |
| Locked crops | When a not-yet-growable crop would improve contribution, growing emits the research or unlock demand through shared acquisition while planting continues from the growable set ("that demand signal needs to get sent even if right now the governor is planting for the immediate forecast"). |
| Field placement | Habitat owns all physical plant including fields; growing expresses land and facility demand to habitat ("habitat needs to own all the physical plant including the fields"). |
| Protected growing | No precondition on colony stage or power surplus; considered whenever the costed numbers work ("Protected doesn't need a precondition if the numbers work"). |
| Mixed strategies | No intent or preference; costed on colony conditions like any other option ("Mixed doesn't have any intent, needs to be costed based on the colony conditions"). |
| Hunt harm scope | Expected harm covers the colony as a whole, not only participants ("colony as a whole"). |
| Taming to slaughter | Enters the same comparison as a hunting option ("i suppose it could be considered hunting"). |
| Sunk effort | Zero weight. Continuation compares remaining work and danger against remaining expected output, which includes a corpse a wounded animal will yield anyway ("as long as we still get the corpse eventually"). |
| Hunt preservation | As written: accounted for in the hunt's expected value; storage capacity is habitat's physical plant ("as written"). |
| Acquiring animals | Herd sizing includes acquiring animals the colony does not yet have, by taming for keeping or by trade ("yes, it includes acquiring animals the colony does not have yet"). |
| Non-production animal value | Herd management owns the population decision and feeding. Each non-production benefit (hauling, defense, carrying, companionship) is priced by the capability that receives it, through shared acquisition. A benefit no capability has priced does not enter the comparison, and that gap is visible in the decomposed decision ("B and yes i'm comfortable"). |
| Pasture and pens | Habitat owns placement; herd management expresses pasture, pen and shelter demand to habitat, consistent with growing ("yes on 4, make them consistent"). |
| Feed versus human food at planting | Equal footing in the shared comparison with no standing planting priority; the asymmetry lives in consequence valuation and hedging, as a scenario under RG-HERD-002 ("equal footing plus that hedging scenario"). |

No fall-through rate, reserve quota, crop list, animal roster, research priority
order or feed-to-food ratio was approved. Research prioritization has no owning
capability yet; the locked-crop decision names only that growing emits the demand
through shared acquisition. Defense, travel and mood have no OpenSpec capability
yet, so their animal benefits are absent from the herd comparison until they
price them; the maintainer accepted that gap explicitly rather than a placeholder
value. The four delta specs were synchronized into standing specifications on
2026-11-03 at the maintainer's direction ("promote"); design and implementation
tasks are not part of this capture.
