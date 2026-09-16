package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RepairerUnavailable mirrors HaulerUnavailable/RescuerUnavailable: the pawn is
// occupied, mid a conflicting player order, or already running this job.
const RepairerUnavailable Reason = "repairer_unavailable"

// StructureIneligible mirrors PatientIneligible: the structure this action
// exists to repair has vanished or no longer needs repair.
const StructureIneligible Reason = "structure_ineligible"

// RepairPawnFacts describes the one already-selected undrafted candidate pawn.
// The planner has already chosen this pawn and structure; EvaluateRepair only
// re-validates the pair immediately before dispatch.
type RepairPawnFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
	ExistingJobDef        domain.Fact[string]
}

// RepairStructureFacts describes the one already-selected damaged structure.
// Exists distinguishes a vanished/destroyed structure from an incomplete
// census; Damaged is false once hit points reach the structure's maximum.
type RepairStructureFacts struct {
	Structure     string
	SnapshotToken string
	Exists        domain.Fact[bool]
	Damaged       domain.Fact[bool]
}

type RepairFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  RepairPawnFacts
	Structure             RepairStructureFacts
	NativeCanTry          domain.Fact[bool]
}

type RepairRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       RepairFacts
}

// EvaluateRepair re-validates one already-selected pawn/structure pair
// immediately before dispatch. Admission proves eligibility now; it does not
// prove the repair job will be issued, accepted or completed.
func EvaluateRepair(r RepairRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	repair, ok := r.Action.Repair()
	canonical, err := domain.NewRepairAction(r.Action.ID(), repair)
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
	if f.Pawn.Pawn != repair.Pawn() || f.Structure.Structure != repair.Structure() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.Structure.SnapshotToken) {
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
		return refuse(RepairerUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	if existingJob == "Repair" {
		return refuse(RepairerUnavailable)
	}
	exists, known := f.Structure.Exists.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !exists {
		return refuse(StructureIneligible)
	}
	damaged, known := f.Structure.Damaged.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !damaged {
		// Already repaired (or never needed repair); nothing left to admit.
		return refuse(StructureIneligible)
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

// RepairCandidateFacts mirrors SecureSuppliesHaulerFacts' eligibility inputs,
// substituting the Construction work-type gate for Hauling:
// an enabled, non-zero-priority Construction work type and a healthy pawn (no
// needed tend, no bleeding). EvaluateRepair re-validates the exact chosen
// pawn/structure pair again immediately before dispatch; this only narrows
// which already-selected structure and pawn become one Repair proposal.
type RepairCandidateFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	ConstructionEnabled                domain.Fact[bool]
}

// SelectRepair pairs the highest-priority damaged structure (callers pass
// structures already ordered the way ReviewUpkeep sorts them: repair
// priority first, then lowest health fraction, then stable ID) with the
// lowest-ID eligible repairer. It is a proposal only; the native preview at
// dispatch still owns whether the job is actually accepted.
func SelectRepair(structures []UpkeepStructure, pawns []RepairCandidateFacts) (UpkeepStructure, domain.PawnID, bool) {
	eligible := func(p RepairCandidateFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		forced, fk := p.PlayerForced.Value()
		needsTend, nk := p.NeedsTend.Value()
		bleeding, bk := p.Bleeding.Value()
		construction, ck := p.ConstructionEnabled.Value()
		if !dk || !wk || !tk || !mk || !fk || !nk || !bk || !ck {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && !needsTend && !bleeding && construction
	}
	var pool []RepairCandidateFacts
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 || len(structures) == 0 {
		return UpkeepStructure{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].Pawn < pool[j].Pawn })
	return structures[0], pool[0].Pawn, true
}
