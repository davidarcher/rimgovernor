package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestUnavailable mirrors PrisonerUnavailable: the quest is no longer an
// exact visible NotYetAccepted offer, or is otherwise unfit for a fresh
// accept write right now.
const QuestUnavailable Reason = "quest_unavailable"

// QuestFacts describes the one already-observed quest offer a goal chose to
// accept, as read from the world-progression census (NativeWorldProgressionObservation.cs's
// Quests()). ChoiceCount is the number of options in the quest's single
// native QuestPart_Choice, or 0 when it carries none. A quest carrying two or
// more separate native QuestPart_Choice parts is never representable here
// (native's own AcceptQuest.Prepare refuses that shape outright,
// "multiple native choice parts require the quest interface"); ChoiceCount
// only ever counts the options within one such part.
type QuestFacts struct {
	Quest             domain.QuestID
	SnapshotToken     string
	State             domain.Fact[string]
	RequiresAccepter  domain.Fact[bool]
	CanAccept         domain.Fact[bool]
	ChoiceCount       domain.Fact[int32]
	EligibleAccepters []domain.PawnID
	HasTradeRequest   domain.Fact[bool]
}

type QuestAcceptFacts struct {
	Snapshot               domain.GenerationSnapshot
	QuestTick, PreviewTick domain.Tick
	Quest                  QuestFacts
	NativeCanTry           domain.Fact[bool]
}

type QuestAcceptRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       QuestAcceptFacts
}

// EvaluateQuestAccept re-validates one already-selected quest and reward
// choice immediately before dispatch, the same shape EvaluatePrisonerInteraction
// uses for its own direct-write order. Admission proves eligibility now; it
// does not prove the write is accepted, and it deliberately never picks a
// quest or reward on its own: the player command must name an exact
// RewardChoice index that falls within the quest's currently observed
// ChoiceCount (whatever that count is -- one option or many), and a
// mismatched, out-of-range or accepter-less RequiresAccepter quest is
// refused rather than guessed, so no reckless colony commitment can be made
// through this boundary. Native alone re-validates the exact same index
// against the live QuestPart_Choice.choices at execute time
// (NativeQuestOperations.Prepare); this admission is a preliminary,
// best-available-facts check, not proof the write will apply.
//
// Freshness is measured on the preview tick, as EvaluateTrade measures it:
// the quest census behind QuestTick is a step-cached observation read, so
// under a running clock the second inspection of a step re-reads the same
// census tick while its preview moves on, and a bound on QuestTick would
// refuse every live dispatch as stale. The preview is the native re-check
// of the same CAS token at its own tick and is never cached.
func EvaluateQuestAccept(r QuestAcceptRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	accept, ok := r.Action.QuestAccept()
	canonical, err := domain.NewQuestAcceptAction(r.Action.ID(), accept)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || f.PreviewTick < f.QuestTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Quest.Quest != accept.Quest() || !validToken(f.Quest.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	state, known := f.Quest.State.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if state != "NotYetAccepted" {
		// Native settled state -- accepted, declined or expired -- not the
		// write itself, resolves the order.
		return refuse(QuestUnavailable)
	}
	canAccept, known := f.Quest.CanAccept.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !canAccept {
		return refuse(NativeIneligible)
	}
	choiceCount, known := f.Quest.ChoiceCount.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	switch {
	case choiceCount == 0 && accept.RewardChoice() != -1:
		return refuse(NativeIneligible)
	case choiceCount > 0 && (accept.RewardChoice() < 0 || accept.RewardChoice() >= choiceCount):
		// The player must name an exact index among the quest's currently
		// observed options; an unselected (-1) or out-of-range index is
		// refused rather than guessed.
		return refuse(NativeIneligible)
	}
	requiresAccepter, known := f.Quest.RequiresAccepter.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if requiresAccepter {
		if accept.AccepterPawn() == "" {
			return refuse(NativeIneligible)
		}
		eligible := false
		for _, pawn := range f.Quest.EligibleAccepters {
			if pawn == accept.AccepterPawn() {
				eligible = true
				break
			}
		}
		if !eligible {
			return refuse(NativeIneligible)
		}
	} else if accept.AccepterPawn() != "" {
		return refuse(NativeIneligible)
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
