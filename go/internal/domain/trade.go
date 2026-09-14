package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// TradeOperationKind selects one of the four native trade sub-operations
// (NativeTradeOperations.cs / bridge/trade.go's TradeWriter): opening a
// session with an already-selected trader/negotiator pair, staging an
// already-computed set of line adjustments, accepting an already-observed
// deal, or ending the session. Trade is RimWorld's single global session
// (only one TradeSession can be open at a time -- see bridge/trade.go's
// package doc), so unlike CaravanDeparture/TravelCaravan this action never
// carries a caller-chosen session id: every operation after Open addresses
// "the" currently open session, exactly the way the native writer does.
type TradeOperationKind string

const (
	TradeOpen     TradeOperationKind = "open"
	TradeSetLines TradeOperationKind = "set_lines"
	TradeAccept   TradeOperationKind = "accept"
	TradeEnd      TradeOperationKind = "end"
)

// TradeEndKind mirrors the native EndTradeKind enum's two supported values:
// cancel abandons the deal; close_dialog sweeps a stale/foreign dialog with
// no goodwill effect (see NativeTradeOperations.PrepareEnd and
// bridge/trade.go's tradeEndCommand/tradeEndEvidence).
type TradeEndKind string

const (
	TradeEndCancel      TradeEndKind = "cancel"
	TradeEndCloseDialog TradeEndKind = "close_dialog"
)

// TradeLine is one requested row adjustment for SetTradeLines: an absolute
// (not relative) target count for one already-identified trade sheet row. It
// mirrors bridge.TradeLineInput exactly, the same way CaravanDeparture's
// CargoItem mirrors its own wire counterpart.
//
// AbsoluteCount is signed, exactly as operations.proto's own int32
// absolute_count is and exactly as Python's TradeLine documents it: positive
// buys (the colony receives), negative sells (the colony gives), zero clears
// the row. "Absolute" distinguishes it from a relative delta (Python's
// `relative: False`), not from a sign. An earlier revision of this type
// refused negatives, which made a sale inexpressible and so made
// trade_policy.py's select_trade -- which produces negative counts for every
// sale -- impossible to port; the bound below is the shape native itself
// accepts.
type TradeLine struct {
	LineID        string
	AbsoluteCount int32
}

// TradeEconomicFloor is one AcceptTrade reserve-stock guard: never sell a
// def below this exact remaining count. Selection of which defs to floor
// (resource policy, goal targets, construction deficits -- trade_policy.py's
// economic_reserves) happens upstream of this boundary; this action only
// carries the already-computed floors.
type TradeEconomicFloor struct {
	DefName string
	Count   int32
}

// Trade is explicit intent to run one of the four trade sub-operations
// against RimWorld's single global session. Fields outside the selected
// Kind's scope are always zero -- the same discipline TravelCaravan's
// sentinel destinationTile uses -- so Action stays comparable and each
// variant carries only the intent it needs. Lines and EconomicFloors are
// canonical JSON-encoded sorted, deduplicated collections, the same
// encoding CaravanDeparture uses for its crew/cargo lists. This action never
// selects a trader, computes which lines to propose, or judges deal value;
// a planner upstream of this boundary makes that choice conservatively (see
// trade_policy.py's select_trade), and native alone re-derives and
// re-checks the live session/sheet at admission time.
type Trade struct {
	kind                  TradeOperationKind
	trader                string
	negotiator            string
	giftMode              bool
	lines                 string
	allowPawns            bool
	expectedDealSignature string
	economicFloors        string
	allowEmpty            bool
	endKind               TradeEndKind
	receiveQuest          bool
}

func canonicalTradeLines(lines []TradeLine) (string, error) {
	rows := append([]TradeLine(nil), lines...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].LineID < rows[j].LineID })
	seen := make(map[string]bool, len(rows))
	for _, l := range rows {
		if !validID(l.LineID) || seen[l.LineID] {
			return "", errors.New("invalid or duplicate trade line")
		}
		seen[l.LineID] = true
	}
	data, err := json.Marshal(rows)
	if err != nil {
		return "", err
	}
	if len(data) > 30000 {
		return "", errors.New("trade lines exceed storage bound")
	}
	return string(data), nil
}

func canonicalTradeFloors(floors []TradeEconomicFloor) (string, error) {
	rows := append([]TradeEconomicFloor(nil), floors...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].DefName < rows[j].DefName })
	seen := make(map[string]bool, len(rows))
	for _, f := range rows {
		if !validID(f.DefName) || f.Count < 0 || seen[f.DefName] {
			return "", errors.New("invalid or duplicate trade economic floor")
		}
		seen[f.DefName] = true
	}
	data, err := json.Marshal(rows)
	if err != nil {
		return "", err
	}
	if len(data) > 30000 {
		return "", errors.New("trade economic floors exceed storage bound")
	}
	return string(data), nil
}

func newTrade(kind TradeOperationKind, trader, negotiator string, giftMode bool, lines []TradeLine, allowPawns bool, expectedDealSignature string, floors []TradeEconomicFloor, allowEmpty bool, endKind TradeEndKind, receiveQuest bool) (Trade, error) {
	switch kind {
	case TradeOpen:
		if !validID(trader) || !validID(negotiator) || trader == negotiator {
			return Trade{}, errors.New("open trade requires a distinct valid trader and negotiator")
		}
		if len(lines) != 0 || allowPawns || expectedDealSignature != "" || len(floors) != 0 || allowEmpty || endKind != "" || receiveQuest {
			return Trade{}, errors.New("open trade carries no other operation's fields")
		}
	case TradeSetLines:
		if trader != "" || negotiator != "" || giftMode || expectedDealSignature != "" || len(floors) != 0 || allowEmpty || endKind != "" || receiveQuest {
			return Trade{}, errors.New("set trade lines carries no other operation's fields")
		}
		if len(lines) == 0 || len(lines) > 256 {
			return Trade{}, errors.New("set trade lines requires a nonempty bounded line list")
		}
	case TradeAccept:
		if trader != "" || negotiator != "" || giftMode || len(lines) != 0 || allowPawns || endKind != "" {
			return Trade{}, errors.New("accept trade carries no other operation's fields")
		}
		if !validID(expectedDealSignature) {
			return Trade{}, errors.New("accept trade requires an expected deal signature")
		}
		if len(floors) > 256 {
			return Trade{}, errors.New("accept trade economic floors exceed bound")
		}
	case TradeEnd:
		if trader != "" || negotiator != "" || giftMode || len(lines) != 0 || allowPawns || expectedDealSignature != "" || len(floors) != 0 || allowEmpty {
			return Trade{}, errors.New("end trade carries no other operation's fields")
		}
		if endKind != TradeEndCancel && endKind != TradeEndCloseDialog {
			return Trade{}, errors.New("end trade requires an explicit cancel or close_dialog kind")
		}
	default:
		return Trade{}, errors.New("invalid trade operation kind")
	}
	encodedLines, err := canonicalTradeLines(lines)
	if err != nil {
		return Trade{}, err
	}
	encodedFloors, err := canonicalTradeFloors(floors)
	if err != nil {
		return Trade{}, err
	}
	return Trade{kind, trader, negotiator, giftMode, encodedLines, allowPawns, expectedDealSignature, encodedFloors, allowEmpty, endKind, receiveQuest}, nil
}

// NewTradeOpen requests opening the single global trade session with one
// already-selected trader settlement and negotiator pawn.
func NewTradeOpen(trader SettlementID, negotiator PawnID, giftMode bool) (Trade, error) {
	return newTrade(TradeOpen, string(trader), string(negotiator), giftMode, nil, false, "", nil, false, "", false)
}

// NewTradeSetLines requests staging an already-computed set of absolute line
// adjustments against the currently open session.
func NewTradeSetLines(lines []TradeLine, allowPawns bool) (Trade, error) {
	return newTrade(TradeSetLines, "", "", false, lines, allowPawns, "", nil, false, "", false)
}

// NewTradeAccept requests accepting the currently open session's deal,
// exactly matching an already-observed deal signature and never selling
// below the given economic floors.
func NewTradeAccept(expectedDealSignature string, floors []TradeEconomicFloor, allowEmpty, receiveQuest bool) (Trade, error) {
	return newTrade(TradeAccept, "", "", false, nil, false, expectedDealSignature, floors, allowEmpty, "", receiveQuest)
}

// NewTradeEnd requests ending the currently open session, either abandoning
// the deal (cancel) or sweeping a stale/foreign dialog with no goodwill
// effect (close_dialog).
func NewTradeEnd(kind TradeEndKind, receiveQuest bool) (Trade, error) {
	return newTrade(TradeEnd, "", "", false, nil, false, "", nil, false, kind, receiveQuest)
}

func (t Trade) Kind() TradeOperationKind { return t.kind }
func (t Trade) Trader() SettlementID     { return SettlementID(t.trader) }
func (t Trade) Negotiator() PawnID       { return PawnID(t.negotiator) }
func (t Trade) GiftMode() bool           { return t.giftMode }

func (t Trade) Lines() []TradeLine {
	var rows []TradeLine
	_ = json.Unmarshal([]byte(t.lines), &rows)
	return rows
}
func (t Trade) AllowPawns() bool              { return t.allowPawns }
func (t Trade) ExpectedDealSignature() string { return t.expectedDealSignature }

func (t Trade) EconomicFloors() []TradeEconomicFloor {
	var rows []TradeEconomicFloor
	_ = json.Unmarshal([]byte(t.economicFloors), &rows)
	return rows
}
func (t Trade) AllowEmpty() bool         { return t.allowEmpty }
func (t Trade) EndKind() TradeEndKind    { return t.endKind }
func (t Trade) ReceiveQuest() bool       { return t.receiveQuest }

const TradeAction ActionKind = "trade"

func NewTradeAction(id ActionID, trade Trade) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := newTrade(trade.kind, trade.trader, trade.negotiator, trade.giftMode, trade.Lines(), trade.allowPawns, trade.expectedDealSignature, trade.EconomicFloors(), trade.allowEmpty, trade.endKind, trade.receiveQuest)
	if err != nil || canonical != trade {
		return Action{}, errors.New("invalid trade")
	}
	return Action{id: id, kind: TradeAction, trade: trade}, nil
}

func (a Action) Trade() (Trade, bool) { return a.trade, a.kind == TradeAction }
