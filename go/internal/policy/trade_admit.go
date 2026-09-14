package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeSessionUnresolved mirrors CaravanRouteUnavailable's naming: refuses a
// set_lines/accept/end Trade action when the persisted session identity its
// dependency Open action's own completion produced cannot yet be resolved --
// Open not observed complete, or its session record not yet durably
// recorded (see store.TradeSession). Never rediscovered or guessed: native
// has no "read the current session" observation.
const TradeSessionUnresolved Reason = "trade_session_unresolved"

// TradeUnavailable mirrors SettlementUnavailable/QuestUnavailable: the
// already-selected trader settlement is not a valid open-trade target right
// now (it is the player's own settlement, or hostile), refused rather than
// guessed. Which trader to open with is chosen upstream of this boundary
// (trade_policy.py's select_trade); this only re-checks the one already
// selected.
const TradeUnavailable Reason = "trade_unavailable"

// TradeOpenFacts describes the one already-selected trader settlement and
// negotiator pawn a goal or player command chose to open a session with, as
// read from the world census (bridge.ReadWorld's SettlementFact) and an
// already-observed pawn token (bridge.ReadPawns). Deal value, which trader
// to pick, or which lines to propose is never decided here -- see
// domain.Trade's own doc comment.
type TradeOpenFacts struct {
	TraderSnapshotToken     string
	TraderIsPlayer          domain.Fact[bool]
	TraderHostile           domain.Fact[bool]
	NegotiatorSnapshotToken string
}

// TradeSessionFacts describes the persisted session identity produced by the
// dependency Open action's own completion evidence (store.TradeSession).
// Resolved is false until that Open action has been observed complete and
// its session durably recorded; it is never true from admission re-deriving
// or guessing a session id/token.
type TradeSessionFacts struct {
	Resolved     bool
	SessionID    string
	SessionToken string
}

// TradeAdmissionFacts bundles the exact per-kind facts EvaluateTrade needs.
// Only the fields for the action's own Kind are consulted; the others are
// simply zero, the same discipline domain.Trade itself uses for its unused
// fields.
type TradeAdmissionFacts struct {
	Snapshot     domain.GenerationSnapshot
	PreviewTick  domain.Tick
	Open         TradeOpenFacts
	Session      TradeSessionFacts
	NativeCanTry domain.Fact[bool]
}

type TradeRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       TradeAdmissionFacts
}

// EvaluateTrade re-validates one already-selected trade sub-operation
// immediately before dispatch, the same shape EvaluateQuestFulfill/
// EvaluateSettlementGift use for their own direct-write orders. Admission
// proves eligibility now; it does not prove the write is accepted. It never
// selects a trader, computes which lines to propose or judges deal value --
// a planner upstream of this boundary makes that choice conservatively (see
// trade_policy.py's economic_reserves/select_trade), and native alone
// re-derives and re-checks the live session/sheet at admission time.
//
// SetTradeLines/AcceptTrade line- and floor-selection heuristics
// (trade_policy.py's select_trade / economic_reserves) are deliberately not
// ported here: they need production-deficit and colony-goal facts no
// existing Go bridge read surfaces. Line/floor content is validated for
// shape by domain.Trade itself at construction; the caller (an upstream
// planner, or an httpapi request for the direct-command path) supplies
// already-computed values, and this admission only re-checks that the
// session those values apply to is exactly the one still open and eligible.
func EvaluateTrade(r TradeRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	trade, ok := r.Action.Trade()
	canonical, err := domain.NewTradeAction(r.Action.ID(), trade)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	switch trade.Kind() {
	case domain.TradeOpen:
		if !validToken(f.Open.TraderSnapshotToken) || !validToken(f.Open.NegotiatorSnapshotToken) {
			return refuse(UnknownFacts)
		}
		player, known := f.Open.TraderIsPlayer.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if player {
			return refuse(TradeUnavailable)
		}
		hostile, known := f.Open.TraderHostile.Value()
		if !known {
			return refuse(UnknownFacts)
		}
		if hostile {
			return refuse(TradeUnavailable)
		}
	case domain.TradeSetLines, domain.TradeAccept, domain.TradeEnd:
		if !f.Session.Resolved || !validToken(f.Session.SessionID) || !validToken(f.Session.SessionToken) {
			return refuse(TradeSessionUnresolved)
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
