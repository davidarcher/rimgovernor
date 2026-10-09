// The trade/execute case drives a whole native trade on
// test/trade_fixture: the negotiator teleported beside a spawned caravan
// trader opens a session, stages one buy line and one sell line, and accepts
// the signed deal. It asserts the accept executed and closed the session with
// colony silver moved, then reopens the session to see the trader's stock and
// the colony's stock moved by the staged counts. The reopened session stages
// a buy the colony cannot afford: the accept is refused and the session ends
// with a cancel.
package trade

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "trade/execute",
		Scope: "Native trade execution: open beside a spawned caravan trader, set one buy and one sell line, accept the signed deal, " +
			"then stock and silver moved and the session closed; an unaffordable deal's accept is refused.",
		Start:  cases.LabStart(),
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runExecute,
	})
}

func runExecute(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, names := s.Harness(), s.Identity(), s.Names()
	for _, required := range []string{"rimgovernor/operations_apply", "rimgovernor/observations_read_trade_session", "rimgovernor/observations_read_trade_sheet"} {
		if !na.Contains(names, required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}

	// routine_setup gives the colony home-area silver and steel to sell and
	// brings an outlander bulk caravan (which buys steel) beside the colony.
	incident, err := h.Call(ctx, "setup", "test/trade_fixture", map[string]any{"action": "routine_setup", "silver": 400, "steel": 150, "medicine": 0})
	if err != nil {
		return err
	}
	var traderID string
	arrival, _ := na.AsMap(incident["arrival"])
	for _, raw := range na.AsSlice(arrival["traderIds"]) {
		traderID = fmt.Sprint(raw)
		break
	}
	if traderID == "" {
		return fmt.Errorf("setup: no trader caravan arrived: %#v", incident)
	}
	report["setup"] = incident

	snapshot, err := h.Call(ctx, "snapshot-colonists", "test/trade_fixture", map[string]any{"action": "snapshot"})
	if err != nil {
		return err
	}
	var negotiatorID string
	for _, raw := range na.AsSlice(snapshot["colonists"]) {
		row, _ := na.AsMap(raw)
		if num(row["social"]) >= 0 {
			negotiatorID = na.AsString(row["id"])
			break
		}
	}
	if negotiatorID == "" {
		return fmt.Errorf("snapshot-colonists: no socially-eligible colonist available: %#v", snapshot)
	}
	teleport, err := h.Call(ctx, "teleport-adjacent", "test/trade_fixture", map[string]any{
		"action": "teleport_adjacent", "traderId": traderID, "pawnId": negotiatorID,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(teleport["success"]); !success {
		return fmt.Errorf("teleport-adjacent refused: %#v", teleport)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	apply := func(label string, step map[string]any) (map[string]any, error) {
		trade := map[string]any{"traderId": traderID, "negotiatorId": negotiatorID}
		for k, v := range step {
			trade[k] = v
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": "trade-execute-" + label, "trade": trade}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	appliedTrade := func(label string, result map[string]any) (map[string]any, error) {
		receipt, _ := na.AsMap(result["applied"])
		applied, ok := na.AsMap(receipt["applied"])
		if !ok {
			return nil, fmt.Errorf("%s: expected an applied outcome, got %#v", label, result)
		}
		observed, _ := na.AsMap(applied["observed"])
		trade, _ := na.AsMap(observed["trade"])
		return trade, nil
	}
	open := func(label string) (string, error) {
		result, err := apply(label, map[string]any{"open": map[string]any{"giftMode": false}})
		if err != nil {
			return "", err
		}
		trade, err := appliedTrade(label, result)
		if err != nil {
			return "", err
		}
		if na.AsString(trade["sessionId"]) == "" {
			return "", fmt.Errorf("%s: expected a session id, got %#v", label, result)
		}
		return na.AsString(trade["sessionId"]), nil
	}
	sheet := func(label, sessionID string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_trade_sheet", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "sessionId": sessionID})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		return observed, err
	}
	rowByID := func(sheet map[string]any, id string) map[string]any {
		for _, raw := range na.AsSlice(sheet["lines"]) {
			row, _ := na.AsMap(raw)
			if na.AsString(row["lineId"]) == id {
				return row
			}
		}
		return nil
	}
	plain := func(row map[string]any) bool {
		currency, _ := na.AsBool(row["currency"])
		pawn, _ := na.AsBool(row["pawn"])
		willTrade, _ := na.AsBool(row["traderWillTrade"])
		return willTrade && !currency && !pawn && na.AsString(row["lineId"]) != ""
	}
	setLines := func(label string, lines []any) error {
		result, err := apply(label, map[string]any{"setLines": map[string]any{"lines": lines}})
		if err != nil {
			return err
		}
		_, err = appliedTrade(label, result)
		return err
	}

	// Step 1: open, stage one buy and one sell, accept.
	sessionID, err := open("open")
	if err != nil {
		return err
	}
	before, err := sheet("sheet", sessionID)
	if err != nil {
		return err
	}
	var buy, sell map[string]any
	for _, raw := range na.AsSlice(before["lines"]) {
		row, _ := na.AsMap(raw)
		if !plain(row) {
			continue
		}
		if num(row["traderCount"]) > 0 && num(row["maximumCount"]) >= 1 && num(row["buyPrice"]) > 0 &&
			(buy == nil || num(row["buyPrice"]) < num(buy["buyPrice"])) {
			buy = row
		}
		if num(row["colonyCount"]) > 0 && num(row["minimumCount"]) <= -1 && num(row["sellPrice"]) > 0 &&
			(sell == nil || -num(row["minimumCount"])*num(row["sellPrice"]) > -num(sell["minimumCount"])*num(sell["sellPrice"])) {
			sell = row
		}
	}
	if buy == nil || sell == nil || na.AsString(buy["lineId"]) == na.AsString(sell["lineId"]) {
		return fmt.Errorf("sheet: expected a distinct buy row and sell row, got buy=%#v sell=%#v", buy, sell)
	}
	// Sell enough to cover the one bought unit, within the colony's stock.
	sellCount := int(math.Min(math.Ceil(num(buy["buyPrice"])/num(sell["sellPrice"]))+1, -num(sell["minimumCount"])))
	buyID, sellID := na.AsString(buy["lineId"]), na.AsString(sell["lineId"])
	report["buy"], report["sell"], report["sellCount"] = buy, sell, sellCount
	if err := setLines("lines", []any{
		map[string]any{"lineId": buyID, "absoluteCount": 1},
		map[string]any{"lineId": sellID, "absoluteCount": -sellCount},
	}); err != nil {
		return err
	}
	staged, err := sheet("staged", sessionID)
	if err != nil {
		return err
	}
	if afford, _ := na.AsBool(staged["colonyCanAfford"]); !afford {
		return fmt.Errorf("staged: the colony cannot afford the staged deal: %#v", staged)
	}
	accepted, err := apply("accept", map[string]any{"accept": map[string]any{"expectedDealSignature": na.AsString(staged["dealSignature"])}})
	if err != nil {
		return err
	}
	deal, err := appliedTrade("accept", accepted)
	if err != nil {
		return err
	}
	report["accept"] = deal
	executed, _ := na.AsBool(deal["executed"])
	traded, _ := na.AsBool(deal["actuallyTraded"])
	closed, _ := na.AsBool(deal["closed"])
	if !executed || !traded || !closed {
		return fmt.Errorf("accept: expected an executed, traded, closed deal, got %#v", deal)
	}
	if num(deal["beforeSilver"]) == num(deal["afterSilver"]) {
		return fmt.Errorf("accept: colony silver did not move: %#v", deal)
	}
	sessionReply, err := h.Wire(ctx, "session-closed", "observations_read_trade_session", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return err
	}
	_, session, err := na.Outcome(sessionReply, "observed")
	if err != nil {
		return err
	}
	if live, _ := na.AsBool(session["open"]); live || na.AsString(session["traderId"]) != "" {
		return fmt.Errorf("session-closed: expected no live trade after accept, got %#v", session)
	}

	// Stock moved: a fresh session shows the trader's bought row and the
	// colony's sold row down by the staged counts.
	sessionID, err = open("reopen")
	if err != nil {
		return err
	}
	after, err := sheet("after", sessionID)
	if err != nil {
		return err
	}
	byDef := func(sheet, row map[string]any) map[string]any {
		want, _ := na.AsMap(row["definition"])
		for _, raw := range na.AsSlice(sheet["lines"]) {
			other, _ := na.AsMap(raw)
			def, _ := na.AsMap(other["definition"])
			if na.DeepEqual(def, want) && na.AsString(other["stuff"]) == na.AsString(row["stuff"]) {
				return other
			}
		}
		return nil
	}
	if row := byDef(after, buy); num(row["traderCount"]) != num(buy["traderCount"])-1 {
		return fmt.Errorf("after: expected the trader's %s down by one from %v, got %#v", buyID, buy["traderCount"], row)
	}
	if row := byDef(after, sell); num(row["colonyCount"]) != num(sell["colonyCount"])-float64(sellCount) {
		return fmt.Errorf("after: expected the colony's %s down by %d from %v, got %#v", sellID, sellCount, sell["colonyCount"], row)
	}

	// Step 2: a buy the colony cannot afford is refused and the session
	// stays open until a cancel ends it.
	var dear map[string]any
	for _, raw := range na.AsSlice(after["lines"]) {
		row, _ := na.AsMap(raw)
		if plain(row) && num(row["maximumCount"]) >= 1 &&
			(dear == nil || num(row["maximumCount"])*num(row["buyPrice"]) > num(dear["maximumCount"])*num(dear["buyPrice"])) {
			dear = row
		}
	}
	if dear == nil {
		return fmt.Errorf("after: no row left to buy: %#v", after)
	}
	if err := setLines("dear-lines", []any{map[string]any{"lineId": na.AsString(dear["lineId"]), "absoluteCount": int(num(dear["maximumCount"]))}}); err != nil {
		return err
	}
	dearSheet, err := sheet("dear", sessionID)
	if err != nil {
		return err
	}
	if afford, _ := na.AsBool(dearSheet["colonyCanAfford"]); afford {
		return fmt.Errorf("dear: the colony can afford the whole of %s; no unaffordable deal to stage: %#v", na.AsString(dear["lineId"]), rowByID(dearSheet, na.AsString(dear["lineId"])))
	}
	refused, err := apply("dear-accept", map[string]any{"accept": map[string]any{"expectedDealSignature": na.AsString(dearSheet["dealSignature"])}})
	if err != nil {
		return err
	}
	refusal, ok := na.AsMap(refused["refused"])
	// Validation catches the shortfall first (INVALID_REQUEST); a deal that
	// slips past it and the game declines is NATIVE_FAILURE.
	if code := na.AsString(refusal["code"]); !ok || code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NATIVE_FAILURE" {
		return fmt.Errorf("dear-accept: expected a refusal, got %#v", refused)
	}
	report["dearRefusal"] = refusal
	ended, err := apply("cancel", map[string]any{"end": map[string]any{"kind": "END_TRADE_KIND_CANCEL"}})
	if err != nil {
		return err
	}
	if _, err := appliedTrade("cancel", ended); err != nil {
		return err
	}
	return nil
}

// num reads a sheet number; an absent field is zero, not AsNumber's -1.
func num(v any) float64 {
	if v == nil {
		return 0
	}
	return na.AsNumber(v)
}
