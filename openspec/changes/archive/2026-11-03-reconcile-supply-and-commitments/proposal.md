# Proposal

## Why

The resource acquisition and herd management specifications confirmed on
2026-11-03 overlap two requirement pages recovered from developer contracts and
never maintainer-confirmed: RG-SUPPLY-001–002 on the supply page and
RG-COMMIT-002 on the commitments page. Acquisition and herds each had two owners.
This change records the maintainer's reconciliation decisions, folds the
surviving intent into the standing specifications and retires or moves the
legacy identities.

## What Changes

- Retire RG-SUPPLY-001. Extraction on the colony map becomes a source under
  RG-ACQUIRE-001 with one scenario covering surface and deep deposits,
  depletion and costed prerequisites. Remote mining is out of scope.
- Retire RG-SUPPLY-002. RG-ACQUIRE-002 widens from food trade to trade in any
  goods, carrying the old requirement's confirmation points as scenarios.
- Drop the morning's ban on food-buying expeditions. A dedicated expedition is
  a costed option for a forecast shortfall in any goods when the forecast places
  the shortfall later than the caravan's return; it is not a response to a
  shortage already present. Absence from colony work and defense is priced per
  participant by the capabilities that lose them.
- Move RG-COMMIT-002 into herd management with its identity preserved; its
  feeding sentence now points at RG-HERD-002. Trim herds from RG-COMMIT-001 so
  joiners and quests remain its subject.
- The supply and commitments pages keep their evidence sections and point the
  retired or moved identities at the standing specifications.

## Capabilities

### Modified Capabilities

- `resource-acquisition`: RG-ACQUIRE-001 gains the extraction scenario;
  RG-ACQUIRE-002 is widened and renamed.
- `herd-management`: RG-COMMIT-002 added as the care and observation requirement.

## Impact

Requirement documents and navigation only. No planner, operation or contract
changes. No implementation-completeness or pass claim follows; the supply page's
S1–S7 and the commitments page's K1–K5 evidence stand as they were, now cited
from the specifications they support.

## Decision provenance and review boundary

Source: the maintainer's reconciliation review in this chat on 2026-11-03,
following the confirmation review of the same day. Each row records the
maintainer's direction and the words that gave it; requirement wording and
scenarios are the assistant's formalization.

| Confirmed decision | Maintainer direction |
| --- | --- |
| RG-SUPPLY-001 | Retired; extraction folds into RG-ACQUIRE-001 as a source ("fold it into acquisition and retire the ID"). |
| Remote mining | Out of scope for now; extraction means deposits on the colony map ("lets leave remote mining out of scope for now"). |
| Extraction scenario | One scenario covering surface and deep deposits, depletion reassessment and costed prerequisites ("one scenario is enough"). |
| Trading expeditions | Supersedes the morning's food-only ban. A dedicated expedition is a costed option for any forecast shortfall, food included, when the forecast shows it far enough ahead for the journey to arrive in time; it is not a response to a shortage already present ("if we're starving, it's usually not a good idea since it takes too long. if we KNOW there will be a shortage and the governor can forecast that, maybe the caravan route is OK and then it could be for any shortfall, costed of course against alternatives"). |
| Ban dropped, gap accepted | The ban is dropped with the widened wording, and the gap that defense does not yet price a participant's absence is accepted explicitly rather than held ("drop the ban with the wording above and accept the defense pricing gap"). |
| Participant costing | Absence is priced per pawn by the capabilities that lose them, so a high-social, low-combat pawn is the cheap caravanner; confirmed as the intended consequence ("I could see a pawn with high social but maybe not a lot of combat skills ... being costed less"). |
| General trade | RG-ACQUIRE-002 widened to all goods; RG-SUPPLY-002 retired with its confirmation points carried as scenarios ("widen acquisition"). |
| Animal care | RG-COMMIT-002 moves into herd management with its ID; feeding points at RG-HERD-002; herds trimmed from RG-COMMIT-001 ("your rec on 4 is good"). |

No caravan size, journey length limit, trade frequency, price threshold or
mining distance cap was approved. Until defense and labor price a participant's
absence per pawn, that cost is absent from the expedition comparison; the
maintainer accepted this gap knowing it biases toward sending caravans. Retired
identities RG-SUPPLY-001 and RG-SUPPLY-002 are never reused.
