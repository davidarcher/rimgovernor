package policy

import (
	"math"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EvaluateRangedDefense admits one observed opponent under an existing exact
// draft claim, mirroring EvaluateMeleeDefense. The pawn must have a ranged
// weapon equipped; native previews still decide whether the shot itself is
// legal (line of sight, distance, ammunition). Explosive-only launchers are
// never treated as admitting this contract.
func EvaluateRangedDefense(r RangedDefenseRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	m, ok := r.Action.RangedAttack()
	canonical, err := domain.NewRangedAttackAction(r.Action.ID(), m)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v, d := r.Progress.View(), r.DraftProgress.View()
	f := r.Facts
	// The admission anchors on the preview tick, the inspection's one live
	// read; the pawn row may come from the step's fact cache up to the
	// planning tolerance behind it under a running window (#306, #323).
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.PawnTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	draft, isDraft := r.DraftProgress.Action().OwnedDraft()
	if !isDraft || d.Action != m.DraftAction() || draft.Pawn() != m.Pawn() || d.Plan != r.Current.Plan || d.Revision != r.Current.Revision || d.Stage != domain.Completed || d.Unresolved {
		return refuse(DraftOwnership)
	}
	cleanup, known := d.DraftCleanup.Value()
	claim, claimed := cleanup.Claim.Value()
	owner, owned := f.Pawn.Owner.Value()
	if !known || cleanup.Stage != domain.DraftCleanupRequired || !claimed || !owned || claim.Action != d.Action || claim.Attempt != d.Attempt || claim.Pawn != m.Pawn() || !claim.Origin.Matches(r.Current) || owner.Claim != claim.Claim || owner.Session != claim.Session {
		return refuse(DraftOwnership)
	}
	if f.PreviewTick < d.Tick {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != m.Pawn() || f.Target.Pawn != m.Target() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.Target.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, hold := range EvaluateEmergency(f.Emergency, r.Current, f.PreviewTick).Holds {
		switch hold.Reason {
		case EmergencyStaleFacts:
			return refuse(StaleFacts)
		case EmergencyUnknownFacts:
			return refuse(UnknownFacts)
		case EmergencyCriticalMedical:
			return refuse(CriticalMedical)
		}
	}
	foundPawn := false
	for _, pawn := range f.Emergency.facts.Colonists {
		if domain.PawnID(pawn.ID) == m.Pawn() {
			foundPawn = true
			for _, fact := range []domain.Fact[bool]{pawn.Dead, pawn.Downed, pawn.Bleeding, pawn.NeedsTend} {
				bad, known := fact.Value()
				if !known {
					return refuse(UnknownFacts)
				}
				if bad {
					return refuse(CriticalMedical)
				}
			}
		}
	}
	if !foundPawn {
		return refuse(UnknownFacts)
	}
	foundTarget := false
	for _, threat := range f.Emergency.facts.Threats {
		dead, dk := threat.Dead.Value()
		down, wk := threat.Downed.Value()
		if domain.PawnID(threat.ID) == m.Target() {
			if !dk || !wk {
				return refuse(UnknownFacts)
			}
			if dead || down {
				return refuse(UnsupportedThreat)
			}
			if threat.Kind == Hostile || threat.Building() {
				foundTarget = true
			}
			continue
		}
		if (dk && dead) || (wk && down) {
			continue
		}
		return refuse(UnsupportedThreat)
	}
	if !foundTarget {
		return refuse(UnsupportedThreat)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Bleeding, f.Pawn.NeedsTend} {
		bad, known := fact.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if bad {
			return refuse(CriticalMedical)
		}
	}
	health, known := f.Pawn.HealthFraction.Value()
	if !known || math.IsNaN(health) || math.IsInf(health, 0) || health < 0 || health > 1 {
		return refuse(UnknownFacts)
	}
	// Native combat health is a float32 comparison, including its exact threshold.
	if health <= float64(float32(0.5005)) {
		return refuse(CriticalMedical)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.FreeColonist, f.Pawn.ViolenceCapable, f.Pawn.RangedWeaponEquipped, f.Pawn.Drafted, f.Target.Dead, f.Target.Downed, f.Target.Hostile, f.NativeCanTry} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	free, _ := f.Pawn.FreeColonist.Value()
	violent, _ := f.Pawn.ViolenceCapable.Value()
	if !free || !violent {
		return refuse(NativeIneligible)
	}
	ranged, _ := f.Pawn.RangedWeaponEquipped.Value()
	if !ranged {
		return refuse(UnsuitableEquipment)
	}
	drafted, _ := f.Pawn.Drafted.Value()
	if !drafted {
		return refuse(DraftOwnership)
	}
	dead, _ := f.Target.Dead.Value()
	down, _ := f.Target.Downed.Value()
	hostile, _ := f.Target.Hostile.Value()
	if dead || down || !hostile {
		return refuse(UnsupportedThreat)
	}
	eligible, _ := f.NativeCanTry.Value()
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
