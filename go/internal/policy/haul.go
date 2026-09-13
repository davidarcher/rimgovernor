package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HaulerUnavailable mirrors DoctorUnavailable/RescuerUnavailable: the pawn is
// occupied, mid a conflicting player order, or already running this job.
const HaulerUnavailable Reason = "hauler_unavailable"

// HaulPawnFacts describes the one already-selected undrafted candidate pawn.
// Unlike SelectTend's colony-wide pairing, the planner has already chosen this
// pawn and thing; EvaluateHaul only re-validates the pair immediately before
// dispatch.
type HaulPawnFacts struct {
	Pawn                      domain.PawnID
	SnapshotToken             string
	Dead, Downed, Drafted     domain.Fact[bool]
	MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                domain.Fact[uint32]
	ExistingJobDef            domain.Fact[string]
}

type HaulFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  HaulPawnFacts
	ThingSnapshotToken    string
	NativeCanTry          domain.Fact[bool]
}

type HaulRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       HaulFacts
}

// EvaluateHaul re-validates one already-selected pawn/thing pair immediately
// before dispatch. Admission proves eligibility now; it does not prove the
// haul job will be issued, accepted or delivered (that is the exact-quantity
// ledger's job, observed later).
func EvaluateHaul(r HaulRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	haul, ok := r.Action.Haul()
	canonical, err := domain.NewHaulAction(r.Action.ID(), haul)
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
	if f.Pawn.Pawn != haul.Pawn() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.ThingSnapshotToken) {
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
	existingJob, known := f.Pawn.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	forced, _ := f.Pawn.PlayerForced.Value()
	if dead || downed {
		return refuse(HaulerUnavailable)
	}
	if drafted || mental || forced || queued != 0 {
		return refuse(PlayerOrder)
	}
	if existingJob == "HaulToCell" || existingJob == "HaulToContainer" {
		return refuse(HaulerUnavailable)
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
