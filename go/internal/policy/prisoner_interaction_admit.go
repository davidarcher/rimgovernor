package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PrisonerUnavailable mirrors HusbandryAnimalUnavailable: the prisoner is
// dead, no longer a colony prisoner, or otherwise cannot receive a fresh
// interaction write right now.
const PrisonerUnavailable Reason = "prisoner_unavailable"

// PrisonerFacts describes the one already-observed prisoner a Population-*
// goal chose a Recruit/MaintainOnly interaction write for.
type PrisonerFacts struct {
	Pawn               domain.PawnID
	SnapshotToken      string
	Dead               domain.Fact[bool]
	Prisoner           domain.Fact[bool]
	Recruitable        domain.Fact[bool]
	CurrentInteraction domain.Fact[domain.PrisonerInteractionMode]
	// Resistance and HeldTicks feed the routine release path only
	// (PrisonerReleaseCandidates): native's remaining recruit resistance and
	// the TimeAsPrisoner record, in ticks. Admission never reads them.
	Resistance domain.Fact[float64]
	HeldTicks  domain.Fact[int64]
}

type PrisonerInteractionFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  PrisonerFacts
	NativeCanTry          domain.Fact[bool]
}

type PrisonerInteractionRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       PrisonerInteractionFacts
}

// EvaluatePrisonerInteraction re-validates one already-selected prisoner and
// interaction pair immediately before dispatch, the same shape
// EvaluateHusbandry uses for its own two direct-write orders. Admission
// proves eligibility now; it does not prove the write is accepted.
func EvaluatePrisonerInteraction(r PrisonerInteractionRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	interaction, ok := r.Action.PrisonerInteraction()
	canonical, err := domain.NewPrisonerInteractionAction(r.Action.ID(), interaction)
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
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != interaction.Pawn() || !validToken(f.Pawn.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	dead, known := f.Pawn.Dead.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if dead {
		return refuse(PrisonerUnavailable)
	}
	prisoner, known := f.Pawn.Prisoner.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !prisoner {
		return refuse(PrisonerUnavailable)
	}
	current, known := f.Pawn.CurrentInteraction.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if current == interaction.Interaction() {
		// Native settled state, not the write, completes the order.
		return refuse(PrisonerUnavailable)
	}
	if interaction.Interaction() == domain.PrisonerInteractionRecruit {
		recruitable, known := f.Pawn.Recruitable.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if !recruitable {
			return refuse(NativeIneligible)
		}
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
