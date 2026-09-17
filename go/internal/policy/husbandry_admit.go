package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HusbandryAnimalUnavailable mirrors GearReplacePawnUnavailable: the animal
// is dead or otherwise cannot receive a fresh settings write right now.
const HusbandryAnimalUnavailable Reason = "husbandry_animal_unavailable"

// HusbandryAnimalFacts describes the one already-selected animal a herd
// target chose a training request or slaughter designation for.
type HusbandryAnimalFacts struct {
	Animal          domain.PawnID
	SnapshotToken   string
	Dead            domain.Fact[bool]
	CanTrain        domain.Fact[bool]
	Learned         domain.Fact[bool]
	SafeToSlaughter domain.Fact[bool]
	Tameable        domain.Fact[bool]
	SafeToRelease   domain.Fact[bool]
}

type HusbandryFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Animal                HusbandryAnimalFacts
	CensusToken           string
	NativeCanTry          domain.Fact[bool]
}

type HusbandryRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       HusbandryFacts
}

// EvaluateHusbandry re-validates one already-selected animal/method pair
// immediately before dispatch, the same shape EvaluateGearReplace uses.
// Admission proves eligibility now; it does not prove the write is accepted.
func EvaluateHusbandry(r HusbandryRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	husbandry, ok := r.Action.Husbandry()
	canonical, err := domain.NewHusbandryAction(r.Action.ID(), husbandry)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Animal.Animal != husbandry.Animal() || !validToken(f.Animal.SnapshotToken) || !validToken(f.CensusToken) {
		return refuse(UnknownFacts)
	}
	dead, known := f.Animal.Dead.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if dead {
		return refuse(HusbandryAnimalUnavailable)
	}
	switch husbandry.Method() {
	case domain.HusbandryTrain:
		canTrain, known := f.Animal.CanTrain.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		learned, known := f.Animal.Learned.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if learned {
			// Native learned state, not the checkbox, completes training.
			return refuse(HusbandryAnimalUnavailable)
		}
		if !canTrain {
			return refuse(NativeIneligible)
		}
	case domain.HusbandrySlaughter:
		safe, known := f.Animal.SafeToSlaughter.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if !safe {
			return refuse(NativeIneligible)
		}
	case domain.HusbandryTame:
		tameable, known := f.Animal.Tameable.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if !tameable {
			return refuse(NativeIneligible)
		}
	case domain.HusbandryRelease:
		safe, known := f.Animal.SafeToRelease.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if !safe {
			return refuse(NativeIneligible)
		}
	default:
		return refuse(NotReady)
	}
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
