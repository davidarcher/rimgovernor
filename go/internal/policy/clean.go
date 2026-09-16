package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CleanerUnavailable mirrors RepairerUnavailable: the pawn is occupied, mid a
// conflicting player order, or already running this job.
const CleanerUnavailable Reason = "cleaner_unavailable"

// FilthIneligible mirrors StructureIneligible: the filth this action exists
// to clean has already been removed (by this pawn, another pawn, or the
// player), so there is nothing left to admit.
const FilthIneligible Reason = "filth_ineligible"

// CleanPawnFacts describes the one already-selected undrafted candidate pawn.
// The planner has already chosen this pawn and filth; EvaluateClean only
// re-validates the pair immediately before dispatch.
type CleanPawnFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
	ExistingJobDef        domain.Fact[string]
}

// CleanFilthFacts describes the one already-selected filth entity. Exists
// distinguishes filth already removed (by any means) from an incomplete
// census; unlike a structure's hit points, filth has no partial-progress
// fact to re-check, only presence.
type CleanFilthFacts struct {
	Filth         string
	SnapshotToken string
	Exists        domain.Fact[bool]
}

type CleanFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  CleanPawnFacts
	Filth                 CleanFilthFacts
	NativeCanTry          domain.Fact[bool]
}

type CleanRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       CleanFacts
}

// EvaluateClean re-validates one already-selected pawn/filth pair immediately
// before dispatch. Admission proves eligibility now; it does not prove the
// clean job will be issued, accepted or completed.
func EvaluateClean(r CleanRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	clean, ok := r.Action.Clean()
	canonical, err := domain.NewCleanAction(r.Action.ID(), clean)
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
	if f.Pawn.Pawn != clean.Pawn() || f.Filth.Filth != clean.Filth() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.Filth.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingJob, known := f.Pawn.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	if dead || downed {
		return refuse(CleanerUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	if existingJob == "Clean" {
		return refuse(CleanerUnavailable)
	}
	exists, known := f.Filth.Exists.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !exists {
		return refuse(FilthIneligible)
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

// CleanCandidateFacts mirrors SecureSuppliesHaulerFacts' eligibility inputs,
// substituting colony_upkeep.py's Cleaning work-type gate for Hauling: an
// enabled, non-zero-priority Cleaning work type and a healthy pawn (no
// needed tend, no bleeding). EvaluateClean re-validates the exact chosen
// pawn/filth pair again immediately before dispatch; this only narrows which
// already-selected filth and pawn become one Clean proposal.
type CleanCandidateFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	CleaningEnabled                    domain.Fact[bool]
}

// SelectClean pairs the highest-priority filth (callers pass filth already
// ordered the way ReviewUpkeep sorts it: critical rooms first, then stable
// ID) with the lowest-ID eligible cleaner. It is a proposal only; the native
// preview at dispatch still owns whether the job is actually accepted.
func SelectClean(filth []UpkeepFilth, pawns []CleanCandidateFacts) (UpkeepFilth, domain.PawnID, bool) {
	eligible := func(p CleanCandidateFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		forced, fk := p.PlayerForced.Value()
		needsTend, nk := p.NeedsTend.Value()
		bleeding, bk := p.Bleeding.Value()
		cleaning, ck := p.CleaningEnabled.Value()
		if !dk || !wk || !tk || !mk || !fk || !nk || !bk || !ck {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && !needsTend && !bleeding && cleaning
	}
	var pool []CleanCandidateFacts
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 || len(filth) == 0 {
		return UpkeepFilth{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].Pawn < pool[j].Pawn })
	return filth[0], pool[0].Pawn, true
}
