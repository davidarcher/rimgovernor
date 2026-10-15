# Essential-service development evidence

[Documentation](../../README.md) · [Ownership](persistence-contracts.md) · [Shared demand](../architecture/colony-forecast.md)

Ordinary `InspectRounds` publishes `RoundsFindings.EssentialServices`. The pure
`ReviewEssentialServices` reads that review's existing owner assessments and
`DerivedDemand`; observation projects existing production benches and power
topology through `RoundsFacts.EssentialInputs`. Immediate reviews do not refresh
ordinary development evidence. No action, admission, new goal store or native
operation is created by assessment.

## Coverage and ownership

| Service | Authoritative evidence |
| --- | --- |
| Food acquisition and access | EnsureFoodSupply; FoodPlan shared allocation and replenishment gap; MaintainFoodStorage and MaintainRefrigeration |
| Food preparation | EnsureCooking; native production recipe availability and cooking floor, enabled available workers, shared fixed-batch ingredient feasibility |
| Medical care | CriticalMedicine, MaintainSurgery and MaintainMedicalReserves; served procedures use the surgery owner's failure cap, native per-doctor chances and ingredient availability |
| Usable shelter and temperature | MaintainHousing, MaintainShelter and EnsureTemperatureSafety, including their observed recovery and latch predicates |
| Power | EnsureBasicPower and the power owner's same-network coverage calculation |
| Material and fuel dependencies | MaintainResource's single merged demand; FuelRunway's native consumption gates, stocks and maintenance horizon |
| Logistics | MaintainRoutes and native per-facility reachability |

Baseline coverage and stress coverage are separate. Known baseline adequacy or
shortfall survives missing counterfactual evidence. A family with absent evidence
is explicitly unknown; no colonists makes colony services inapplicable. Fuel
consumers retain their own evidence even when no colonists are present.

Single-dependency checks remove one procedure doctor, cooking worker/bench,
reachable logistics worker or power generator at a time. Procedure alternatives
use the existing surgery acceptance rule; meal recipes use native skill floors
and the existing fixed-batch material feasibility owner. Multiple recipes or
workers can resolve recipe eligibility. Eligibility alone does not certify replacement
throughput: successful alternate eligibility retains an explicit unknown stress
result until service capacity is established. Normal productive cooking comparison
uses [native skill-dependent durations](skill-learning.md#productive-cooking-in-normal-selection)
for actual scheduled meals. Journal pawn claims are unavailable to
cooking capability checks. Quest-protected patients produce no surgery goals.
Checks have a deterministic scenario budget (128 by default), expose truncation,
and never enumerate combinations or assign failure probabilities.

## Targets and lifecycle

A proposal references the owning Concern and subject. Its stable ID excludes
severity, tick and candidate choice. All requirements are retained when evidence
from several services has the same owner and subject; shared material/fuel demand
is not added again. Stress evidence identifies the unavailable dependency and
remains distinct from a currently failing service. Continue, defer, alternative
chains, capability development, facility restoration and acquisition are classes
of alternatives to consider, not approved actions or ranked winners.

The target is a named owner recovery or mechanical feasibility predicate, never
an arbitrary skill level, historical maximum or unlocked training ceiling.
Proposals are recomputed from fresh facts. `ReassessDevelopment` distinguishes
active, satisfied, obsolete and unknown using the current coverage. Missing
evidence, older reviews and missing stress results cannot establish satisfaction.
Native observations establish eligibility and owner recovery; projected learning
or accepted orders cannot do so. Live consumers must still compare consequences,
protect player obligations and establish actual capacity and legality.

## Explicit bounds

The cooking check establishes eligibility and materials for one ordinary-meal
iteration; it does not establish throughput, delivery completion or a future
food runway. FoodPlan remains the replenishment owner. Ingredient search limits
and incomplete facts cannot certify infeasibility. Medical eligibility uses
native procedure-specific evidence; it does not forecast diseases or create
generic doctor skill requirements.

The current observations do not bind food portfolio channels and thermal/shelter
service recovery to complete counterfactual physical dependencies. Those stress
results name missing owner dependency evidence. Power generator checks reuse
current network output, not a future weather simulation; consumer-to-essential-
service membership remains explicitly unknown. Decorative loads are not silently
classified as food or heat. Fuel per-use/rain-sensitive consumers retain the
fuel owner's unsupported-rate findings. These limits do not erase the baseline
assessments already provided by those service owners.

All evidence is transient and attached to the full review. Existing Concern
ownership and the Method journal remain the only owners of admitted attempts.
No assessment assigns a value to XP or authorizes speculative spending.
