package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SettlementUnavailable mirrors QuestUnavailable: the caravan is not
// currently, exactly settled at the intended settlement (wrong target,
// moving, own settlement, or no settlement at all), so no gift write is
// possible right now regardless of goodwill.
const SettlementUnavailable Reason = "settlement_unavailable"

// InsufficientReserve refuses a gift the caravan cannot exactly afford: the
// requested silver exceeds the caravan's last-observed silver, or the
// reserve itself is unknown. Never partially fulfilled, never guessed.
const InsufficientReserve Reason = "insufficient_reserve"

// SettlementGiftFacts describes the one already-observed, already-visiting
// caravan and the one settlement/faction it is intended to gift, as read
// from the world-progression census (caravan position/crew/silver) and the
// world census (settlement/faction relation, both CAS-tokened). CrewIDs is
// the caravan's actual current crew, to be checked for exact set-equality
// against the gift's expected crew -- the same membership discipline
// QuestAccept's EligibleAccepters check uses, applied to the whole roster
// instead of one accepter.
type SettlementGiftFacts struct {
	Caravan      domain.CaravanID
	CaravanToken string
	Moving       domain.Fact[bool]
	CrewIDs      domain.Fact[[]domain.PawnID]
	Silver       domain.Fact[int32]

	Settlement domain.SettlementID
	AtTarget   domain.Fact[bool] // the settlement currently sitting at the caravan's tile is exactly the intended one

	Faction      domain.FactionID
	FactionToken string
	Player       domain.Fact[bool]
	Hostile      domain.Fact[bool]
	Goodwill     domain.Fact[int32]
}

type SettlementGiftAdmissionFacts struct {
	Snapshot                          domain.GenerationSnapshot
	CaravanTick, WorldTick, PreviewTick domain.Tick
	Gift                              SettlementGiftFacts
	NativeCanTry                      domain.Fact[bool]
}

type SettlementGiftRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       SettlementGiftAdmissionFacts
}

// EvaluateSettlementGift re-validates one already-selected caravan-to-
// settlement gift immediately before dispatch, the same shape
// EvaluateQuestAccept uses for its own direct-write order. Admission proves
// eligibility now; it does not prove the write is accepted, and it
// deliberately never chooses a caravan, settlement, faction or silver amount
// on its own: unknown faction standing, an unconfirmed silver reserve, or a
// wrong/missing settlement target is refused rather than guessed, so no
// unrecoverable goodwill or silver commitment can be made through this
// boundary.
func EvaluateSettlementGift(r SettlementGiftRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	gift, ok := r.Action.SettlementGift()
	canonical, err := domain.NewSettlementGiftAction(r.Action.ID(), gift)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 ||
		f.CaravanTick < r.MinimumTick || f.WorldTick < r.MinimumTick || f.PreviewTick < f.CaravanTick || f.PreviewTick < f.WorldTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	minObserved := f.CaravanTick
	if f.WorldTick < minObserved {
		minObserved = f.WorldTick
	}
	if minObserved < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Gift.Caravan != gift.Caravan() || !validToken(f.Gift.CaravanToken) || f.Gift.Settlement != gift.Settlement() ||
		f.Gift.Faction != gift.Faction() || !validToken(f.Gift.FactionToken) {
		return refuse(UnknownFacts)
	}
	moving, known := f.Gift.Moving.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if moving {
		return refuse(SettlementUnavailable)
	}
	crew, known := f.Gift.CrewIDs.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	expected := gift.CrewIDs()
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
	atTarget, known := f.Gift.AtTarget.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !atTarget {
		return refuse(SettlementUnavailable)
	}
	player, known := f.Gift.Player.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if player {
		return refuse(SettlementUnavailable)
	}
	hostile, known := f.Gift.Hostile.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if hostile {
		return refuse(NativeIneligible)
	}
	goodwill, known := f.Gift.Goodwill.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if goodwill >= 100 {
		// Native itself will not register a further goodwill gain; refuse
		// rather than spend silver for no confirmed effect.
		return refuse(NativeIneligible)
	}
	silver, known := f.Gift.Silver.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if silver < gift.Silver() {
		return refuse(InsufficientReserve)
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
