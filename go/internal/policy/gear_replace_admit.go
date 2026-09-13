package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// GearReplacePawnUnavailable mirrors EquipPawnUnavailable: the pawn is dead,
// downed or otherwise cannot be given a fresh order right now.
const GearReplacePawnUnavailable Reason = "gear_replace_pawn_unavailable"

// GearReplacePawnFacts describes the one already-selected undrafted pawn
// gear_upkeep.compile_upkeep chose a replacement for.
type GearReplacePawnFacts struct {
	Pawn                      domain.PawnID
	SnapshotToken             string
	Dead, Downed, Drafted     domain.Fact[bool]
	MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                domain.Fact[uint32]
	ExistingJobDef            domain.Fact[string]
}

type GearReplaceFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  GearReplacePawnFacts
	ThingSnapshotToken    string
	LoadoutToken          string
	NativeCanTry          domain.Fact[bool]
}

type GearReplaceRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       GearReplaceFacts
}

// EvaluateGearReplace re-validates one already-selected pawn/item pair
// immediately before dispatch, the same shape EvaluateEquip uses. Admission
// proves eligibility now; it does not prove the wear job will be issued,
// accepted or that the pawn ends up wearing the item (that is observed later).
func EvaluateGearReplace(r GearReplaceRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	replace, ok := r.Action.GearReplace()
	canonical, err := domain.NewGearReplaceAction(r.Action.ID(), replace)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
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
	if f.Pawn.Pawn != replace.Pawn() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.ThingSnapshotToken) || !validToken(f.LoadoutToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState, f.Pawn.PlayerForced} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	queued, known := f.Pawn.QueuedJobs.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if _, known = f.Pawn.ExistingJobDef.Value(); !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	forced, _ := f.Pawn.PlayerForced.Value()
	if dead || downed {
		return refuse(GearReplacePawnUnavailable)
	}
	if drafted || mental || forced || queued != 0 {
		return refuse(PlayerOrder)
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
