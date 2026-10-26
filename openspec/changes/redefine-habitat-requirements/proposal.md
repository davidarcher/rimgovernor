# Proposal

## Why

The existing habitat requirements describe sleeping space, essential services and
useful room development, but do not explicitly cover the colony's whole physical
plant. Capture the maintainer-confirmed scope and tradeoffs without turning a
requirements category into an independent planner or claiming implementation completeness.

## What Changes

- Draft habitat requirements for accommodation, facilities and activity sites,
  fields, growing zones, pens, access, utilities, defensive infrastructure,
  maintenance and redevelopment.
- Preserve RG-HABITAT-001–003 and their core outcomes, broaden accommodation beyond
  colonists, and add requirements for the newly explicit responsibilities.
- Compare bedrooms and barracks, spots and workstations, temporary and permanent
  care locations, spare capacity and redevelopment by their complete consequences.
- Preserve functional requirements while treating comfort, efficiency and reserves
  as costed improvements; prescribe no finished-base design or upgrade ladder.
- Record the agreed architecture boundary: Families propose useful alternatives,
  shared spatial policy develops feasible arrangements and their consequences,
  shared comparison selects complete alternatives, admitted Methods own work and
  Hands executes it. Habitat is not another approval stage.
- Retain existing source/test inspection and execution evidence separately.

## Capabilities

### New Capabilities

- `habitat`: provide, operate and adapt the physical infrastructure supporting
  colony occupants and useful activities. This is new to OpenSpec, not a claim
  that physical planning is new to the product.

### Modified Capabilities

None. Food, protection, wellbeing, supply and commitments retain their outcome
ownership. Their demands and effects inform habitat alternatives; this draft
does not rewrite their policies.

## Impact

This capture adds a proposal and a draft capability specification only. It does
not replace the standing requirements in `docs/requirements/habitat.md`, migrate
the standing capability, change code, or create implementation tasks. The existing
[habitat page](../../../docs/requirements/habitat.md) retains H1–H7 inspection,
test provenance and evidence limits. No new game run or implementation audit is
claimed. The change remains active for review and later planning.

Future implementation must trace alternatives through typed facts, pure policy,
shared demand, comparison, Method admission, Hands, native operations and observed
outcomes. `LayoutPlan` remains the owner of spatial intent. The agreed boundary
does not authorize a second spatial store, domain-specific placement copies or a
parallel winner-selection loop. Existing facility ladders and guest bed limits
must be assessed against the draft rather than treated as product requirements.

## Decision provenance and review boundary

Source: the maintainer's habitat exploration in this chat on 2026-10-25–26,
ending with the explicit drafting request, "draft away". The decisions below
record confirmed direction. Requirement wording and concrete scenarios are the
assistant's draft formalization, not separately approved tactics or test results.

| Confirmed direction | Source in the discussion |
| --- | --- |
| Whole physical plant, including agriculture and pens | "yes include them" following the scope discussion |
| Accommodation can remain a barracks | "a well-decorated barracks may be fine for pawns, tradeoffs will need to be costed by the bot" |
| Complete alternatives with shared spatial evaluation and comparison | "third sounds good" after reviewing the three architecture options |
| Continuous improvement without a prescribed finished base | "yes" to the adequacy-versus-improvement recommendation |
| Spare capacity from foreseeable demand, shortage costs and temporary alternatives | "that fits" |
| Compare repair, replacement, relocation and retirement | "yup" |
| Resilience from consequences of interrupted services, without universal uptime or temperature targets | "yes fits" |
| Costed defensive arrangements without a mandatory killbox or perimeter style | "yup" |
| Redevelopment includes transition costs; idle facilities need not be demolished | "yes" |
| Functional requirements differ from preferences; observed service establishes completion | "yes" to the final requirements-versus-preferences discussion |

The discussion explicitly included prisoners, animals, guests and other dependents,
ordinary-bed medical conversion, and field treatment using a medical sleeping
spot. The maintainer identified surgery in a separate hospital bed as a likely
better arrangement, not an unconditional location requirement. The draft preserves
these alternatives without claiming every example is already implemented or
requiring the advanced field tactic as the first implementation milestone.

No fixed spare-capacity quota, universal room-temperature target, mandatory private
bedroom, room catalogue, workstation upgrade sequence or guaranteed immunity to
disruption was approved. Shared decisions must include construction and operating
costs, delayed benefits and service disruption. The specification does not choose
a forecast horizon, search algorithm or particular valuation formula.

## Responsibility and evidence boundary

Habitat includes rooms, outdoor facilities, furniture, workstations, activity
spots, storage, fields, growing zones, animal pens, utilities, access and defensive
structures. Food, medical, production, social, ideology, animal and protection
policies supply the demands and domain effects those arrangements serve. Accepting
or continuing a hosting obligation belongs to commitments; habitat accounts for
the occupants while that obligation exists. Crop and herd choices remain with
their domain policies, informed by feasible spatial alternatives.

RG-HABITAT-001–003 preserve the existing accommodation, essential-service and useful
development identities. RG-HABITAT-004–011 make the agreed scope and tradeoffs
explicit. The scenarios are review examples, not execution evidence. The draft
remains separate from standing requirements until explicitly adopted.
