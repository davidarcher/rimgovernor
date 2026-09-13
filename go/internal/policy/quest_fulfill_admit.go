package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// QuestFulfillFacts describes the one already-accepted quest and one
// already-visiting caravan a goal chose to fulfill a native settlement
// trade-request objective with, as read from the world-progression census
// (NativeWorldProgressionObservation.cs's Quests()/Caravans()).
// AtTarget is true only when the quest carries exactly one trade
// destination tile and the caravan is stationary at that exact tile; native
// alone re-derives and re-checks the exact requested resource/count against
// the live TradeRequestComp, so no resource facts are carried here.
type QuestFulfillFacts struct {
	Quest           domain.QuestID
	QuestToken      string
	State           domain.Fact[string]
	HasTradeRequest domain.Fact[bool]

	Caravan      domain.CaravanID
	CaravanToken string
	Moving       domain.Fact[bool]
	CrewIDs      domain.Fact[[]domain.PawnID]

	AtTarget domain.Fact[bool]
}

type QuestFulfillAdmissionFacts struct {
	Snapshot                    domain.GenerationSnapshot
	QuestTick, CaravanTick, PreviewTick domain.Tick
	Fulfill                     QuestFulfillFacts
	NativeCanTry                domain.Fact[bool]
}

type QuestFulfillRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       QuestFulfillAdmissionFacts
}

// EvaluateQuestFulfill re-validates one already-selected quest/caravan
// fulfillment immediately before dispatch, the same shape
// EvaluateSettlementGift uses for its own direct-write order. Admission
// proves eligibility now; it does not prove the write is accepted, and it
// deliberately never chooses a quest or caravan on its own: an
// unaccepted/settled quest, a missing or degraded trade objective, a
// caravan not exactly at the requested settlement, or a crew mismatch is
// refused rather than guessed, so no wrong-map or wrong-caravan write can
// be made through this boundary.
func EvaluateQuestFulfill(r QuestFulfillRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	fulfill, ok := r.Action.QuestFulfill()
	canonical, err := domain.NewQuestFulfillAction(r.Action.ID(), fulfill)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 ||
		f.QuestTick < r.MinimumTick || f.CaravanTick < r.MinimumTick || f.PreviewTick < f.QuestTick || f.PreviewTick < f.CaravanTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	minObserved := f.QuestTick
	if f.CaravanTick < minObserved {
		minObserved = f.CaravanTick
	}
	if minObserved < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Fulfill.Quest != fulfill.Quest() || !validToken(f.Fulfill.QuestToken) || f.Fulfill.Caravan != fulfill.Caravan() || !validToken(f.Fulfill.CaravanToken) {
		return refuse(UnknownFacts)
	}
	state, known := f.Fulfill.State.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if state != "Ongoing" {
		// Native settled state -- not yet accepted, already ended, or
		// expired -- not the write itself, resolves the order.
		return refuse(QuestUnavailable)
	}
	hasTradeRequest, known := f.Fulfill.HasTradeRequest.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !hasTradeRequest {
		return refuse(QuestUnavailable)
	}
	moving, known := f.Fulfill.Moving.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if moving {
		return refuse(SettlementUnavailable)
	}
	crew, known := f.Fulfill.CrewIDs.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	expected := fulfill.CrewIDs()
	if len(crew) != len(expected) {
		return refuse(SettlementUnavailable)
	}
	seen := make(map[domain.PawnID]bool, len(expected))
	for _, pawn := range expected {
		seen[pawn] = true
	}
	for _, pawn := range crew {
		if !seen[pawn] {
			return refuse(SettlementUnavailable)
		}
		delete(seen, pawn)
	}
	if len(seen) != 0 {
		return refuse(SettlementUnavailable)
	}
	atTarget, known := f.Fulfill.AtTarget.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !atTarget {
		return refuse(SettlementUnavailable)
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
