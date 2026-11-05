# Proposal

## Why

Combat had no requirement of its own. RG-PROTECT-001 says a recognised threat
is answered without a comparison and that readiness, withdrawal and abandoned
work are priced; RG-HABITAT-009 owns defensive works. Nothing said how a fight
is fought, who fights, when the colony falls back, or what happens to a downed
enemy, while the brain already carries a tactics layer with roughly twenty
named heuristics and a post-fight step that kills downed raiders by default.
The maintainer walked through combat on 2026-11-05. This change records those
decisions as a standing combat capability.

## What Changes

- Add the `combat` capability with RG-COMBAT-001–004: a fight plan chosen by
  expected loss across the colony, fighters chosen by per-pawn price, the plan
  carried out as one plan and reconsidered from fresh observations, and the
  fight ended by priced decisions on pursuit and downed enemies.
- Threat type is an input to the comparison, not a selector of a fixed tactic.
  Every plan carries a fallback condition priced per fight; running, sealing in
  and abandoning the map are plans in the same comparison.
- The player "protected pawn" draft preference is dropped. The fixed
  half-health draft cutoff and the named tactical constants become initial
  candidates, not rules.
- The military family's arming hold (`unsupported_forecast_equipment`) is
  lifted: readiness is equipment demand under RG-PROTECT-001, so the hold is a
  defect to remove, not a known gap to keep.
- Post-fight handling becomes a priced decision among kill, capture, treat and
  release, and leave; the Luciferium-addiction capture special case goes away.
- Epic #3163 is closed by the maintainer's decision and will be re-opened
  under this specification.

## Capabilities

### New Capabilities

- `combat`: RG-COMBAT-001–004.

### Modified Capabilities

- `protection`: purpose paragraph names combat as the owner of tactics; this
  is a direct edit of the standing specification because the archive keeps an
  existing purpose as authoritative.

## Design direction

The maintainer proposed running the colony's fighters the way the game runs
raiders: a Lord, a small state machine of toils with triggered transitions,
each toil giving every pawn a duty that picks its jobs tick by tick. The
direction recorded here is to reuse that framework with governor-authored,
defender-shaped toils and duties, not the raid toils themselves, which are
written for disposable attackers. The brain compares whole plans and their
fallback triggers and hands the chosen one to the Lord; the Lord's state is
readable so each review reconsiders from fresh observations. This is a design
choice for the implementing change, not a requirement; the requirements say
what a fight must deliver.

## Code findings for the implementing change

Reported from a read of the current combat code on 2026-11-05; indicative,
not inspected claim by claim.

- `CombatDecideRules.DecideCombat` picks one of twelve named tactics per round
  and drives them with named constants (door ranges, kite speed ratio and
  range, flank size, bleed and heal thresholds, rescue doctor skill, pod
  responders per pod, manhunter wave size). Under RG-COMBAT-001 they are
  initial candidates with provenance, not rules.
- `CombatSquadRoster` excludes any pawn at or below native health 0.5005 and
  `PreferenceRules.HeldBackFromDraft` honours a player protected flag. Both go
  under RG-COMBAT-002.
- `MilitaryFamily.Propose` holds `military/defense` as
  `unsupported_forecast_equipment` and takes no arming action. Lifted.
- `DefensePlanner.PostFight` strips downed raiders, routes
  Luciferium-addicted ones to capture and has the nearest free colonist kill
  the rest. The addiction case is backwards (such a prisoner dies without a
  continuing supply) and the default is unpriced; RG-COMBAT-004 replaces both.
- No colony-wide withdrawal or evacuation plan exists beyond a shelter tactic
  and hurt-pawn retreat.

## Impact

Requirement documents and navigation only. No planner, operation or contract
changes in this change. The protection evidence page keeps P1–P6 and now cites
the combat specification for tactics. No implementation-completeness or pass
claim follows.

## Decision provenance and review boundary

Source: the maintainer's combat review in this chat on 2026-11-05. Each row
records the maintainer's direction and the words that gave it; requirement
wording and scenarios are the assistant's formalization.

| Confirmed decision | Maintainer direction |
| --- | --- |
| Execution framework | Run colony fighters like the raid AI, reusing the Lord framework with our own toils ("RimWorld has a Raid AI that raider pawns use. What if we did a similar thing for the combat tactics? I know the raider AI is a little derpy."). |
| Tactic choice | Tactics chosen by expected loss across the colony; current heuristics are initial candidates, not the rule ("agreed"). |
| Who fights | Per-pawn pricing; the player protected flag is dropped ("never heard of this protected flag. drop it."). |
| Fallback | Every plan carries a priced fallback condition; running, sealing in and abandoning the map compete in the same comparison ("confirm"). |
| Arming hold | Lifted; readiness is equipment demand ("lifted -- that was probably a ... 'it's done boss!' but it wasn't done."). |
| After the fight | Kill, capture, treat and release, or leave are priced; the Luciferium capture case is wrong ("wtf? we capture luciferium-addicted? that sounds wrong. yes price it."). |
| Threat types | An input to the comparison, not a fixed tactic per type ("6 confirmed"). |
| Violence from inside | Mental breaks and prison breaks are answered under the protection override with the plan priced ("7 confirmed"). |
| Animals, prisoners, guests | Attack animals fight as priced participants, other animals are moved out, prisoners are never armed, guests are not counted on or ordered ("thats good"). |
| Pursuit and the end | Pursuit is priced with the post-fight decisions and the colony chases when it pays; the fight ends on observed no hostile standing ("absolutely chase, confirmed"). |
| Epic #3163 | Closed, to be re-opened under the combat specification ("close it, we will re-open it under the combat spec"). |

No draft health cutoff, loss fraction, kite ratio, pursuit distance, capture
rule or execution policy was approved. The defense pricing gap accepted on
2026-11-03 still applies to a fighter's absence from other work until defense
prices it per pawn. The Lord framework is a direction, not an approved
implementation.
