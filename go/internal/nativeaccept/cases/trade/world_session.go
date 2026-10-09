package trade

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	for _, channel := range []string{"settlement", "orbital"} {
		cases.Register(cases.Case{
			Name:        "trade/" + channel + "-session",
			Scope:       "Core " + channel + " trade: shared open/read/stage/accept/end, cancel without buying, native inventory/silver transfer and closed session. A Go snapshot cannot prove vanilla settlement inventory ownership or orbital comms and drop-pod delivery. Opening and staging supply no goods; settlement purchases remain away from home.",
			Start:       cases.Fixture{On: cases.LabStart(), Op: "test/trade_fixture", Args: map[string]any{"action": "session_setup", "channel": channel}},
			RequiredOps: []string{"test/trade_fixture"}, Quiet: na.QuietRequired,
			Budget: cases.LabBudget, Crew: cases.Crew{Size: 3},
			// Two ordered sessions keep local baseline state and cannot resume mid-deal.
			NoCheckpoint: true,
			Run:          func(ctx context.Context, s cases.Session) error { return runWorldSession(ctx, s, channel) },
		})
	}
}

func runWorldSession(ctx context.Context, s cases.Session, channel string) error {
	h, identity := s.Harness(), s.Identity()
	prepared := s.Prepared()
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("session_setup: %#v", prepared)
	}
	seller, caravan, pawn := na.AsString(prepared["traderId"]), na.AsString(prepared["caravanId"]), na.AsString(prepared["pawnId"])
	var target map[string]any
	if channel == "settlement" {
		target = map[string]any{"settlement": map[string]any{"settlementId": seller, "caravanId": caravan}}
	} else {
		target = map[string]any{"orbitalShip": map[string]any{"shipId": seller}}
	}
	s.Report()["prepared"] = prepared
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	apply := func(label string, step map[string]any) (map[string]any, error) {
		intent := map[string]any{"target": target, "negotiatorId": pawn}
		for k, v := range step {
			intent[k] = v
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": "world-session-" + label, "trade": intent}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	applied := func(label string, result map[string]any) (map[string]any, error) {
		receipt, _ := na.AsMap(result["applied"])
		outcome, ok := na.AsMap(receipt["applied"])
		if !ok {
			return nil, fmt.Errorf("%s: expected applied: %#v", label, result)
		}
		observed, _ := na.AsMap(outcome["observed"])
		trade, _ := na.AsMap(observed["trade"])
		return trade, nil
	}
	read := func(label, operation string, extra map[string]any) (map[string]any, error) {
		args := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}
		for k, v := range extra {
			args[k] = v
		}
		reply, err := h.Wire(ctx, label, operation, args)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		return observed, err
	}
	inventory := func(label string) (map[string]any, error) {
		return h.Call(ctx, label, "test/trade_fixture", map[string]any{"action": "session_inventory", "channel": channel, "traderId": seller, "caravanId": caravan})
	}
	unchanged := func(label string, before map[string]any) error {
		after, err := inventory(label)
		if err != nil {
			return err
		}
		for _, field := range []string{"silver", "medicine", "components", "sellerComponents", "homeComponents"} {
			if num(before[field]) != num(after[field]) {
				return fmt.Errorf("%s moved %s before acceptance: before=%#v after=%#v", label, field, before, after)
			}
		}
		return nil
	}
	closed := func(label string) error {
		state, err := read(label, "observations_read_trade_session", nil)
		if err != nil {
			return err
		}
		if open, _ := na.AsBool(state["open"]); open || na.AsString(state["sessionId"]) != "" {
			return fmt.Errorf("%s: session still open: %#v", label, state)
		}
		return nil
	}
	open := func(label string) (string, error) {
		result, err := apply(label, map[string]any{"open": map[string]any{"giftMode": false}})
		if err != nil {
			return "", err
		}
		if _, err := applied(label, result); err != nil {
			return "", err
		}
		var sessionID string
		observe := func(ctx context.Context) (string, bool, error) {
			state, err := read(label+"-session", "observations_read_trade_session", nil)
			if err != nil {
				return "", false, err
			}
			sessionID = na.AsString(state["sessionId"])
			isOpen, _ := na.AsBool(state["open"])
			return sessionID, isOpen && sessionID != "", nil
		}
		// Orbital opening orders a real UseCommsConsole job; its receipt is not an open session.
		if _, ready, err := observe(ctx); err != nil || ready {
			return sessionID, err
		}
		if _, err := na.RunUntil(ctx, h, label+"-contact", 2500, na.Wait{Stall: na.StallBudget()}, observe); err != nil {
			return "", err
		}
		return sessionID, nil
	}
	sheet := func(label, session string) (map[string]any, error) {
		return read(label, "observations_read_trade_sheet", map[string]any{"sessionId": session})
	}
	stage := func(label string, lines []any) error {
		result, err := apply(label, map[string]any{"setLines": map[string]any{"lines": lines}})
		if err != nil {
			return err
		}
		_, err = applied(label, result)
		return err
	}
	line := func(id string, count int) any { return map[string]any{"lineId": id, "absoluteCount": count} }
	find := func(sheet map[string]any, name string) map[string]any {
		for _, raw := range na.AsSlice(sheet["lines"]) {
			row, _ := na.AsMap(raw)
			def, _ := na.AsMap(row["definition"])
			if na.AsString(def["defName"]) == name {
				return row
			}
		}
		return nil
	}
	cancel := func(label string) error {
		result, err := apply(label, map[string]any{"end": map[string]any{"kind": "END_TRADE_KIND_CANCEL"}})
		if err != nil {
			return err
		}
		if _, err := applied(label, result); err != nil {
			return err
		}
		return closed(label + "-closed")
	}
	before, err := inventory("before")
	if err != nil {
		return err
	}
	if num(before["silver"]) != 2000 || num(before["medicine"]) != 6 || num(before["components"]) != 0 || num(before["sellerComponents"]) != 20 {
		return fmt.Errorf("invalid pinned inventory: %#v", before)
	}
	sessionID, err := open("browse-open")
	if err != nil {
		return err
	}
	offers, err := sheet("browse-sheet", sessionID)
	if err != nil {
		return err
	}
	component, medicine := find(offers, "ComponentIndustrial"), find(offers, "MedicineIndustrial")
	if component == nil || num(component["buyPrice"]) <= 0 || num(component["maximumCount"]) < 1 || medicine == nil || num(medicine["minimumCount"]) > -1 {
		return fmt.Errorf("missing live priced stock: %#v", offers)
	}
	if err := unchanged("opened-no-delivery", before); err != nil {
		return err
	}
	if err := stage("browse-stage", []any{line(na.AsString(component["lineId"]), 1)}); err != nil {
		return err
	}
	if err := unchanged("staged-no-delivery", before); err != nil {
		return err
	}
	if err := cancel("browse-cancel"); err != nil {
		return err
	}
	if err := unchanged("cancel-no-purchase", before); err != nil {
		return err
	}

	sessionID, err = open("purchase-open")
	if err != nil {
		return err
	}
	offers, err = sheet("purchase-sheet", sessionID)
	if err != nil {
		return err
	}
	component = find(offers, "ComponentIndustrial")
	componentID := na.AsString(component["lineId"])
	accept := func(label string) (map[string]any, error) {
		staged, err := sheet(label+"-sheet", sessionID)
		if err != nil {
			return nil, err
		}
		return apply(label, map[string]any{"accept": map[string]any{"expectedDealSignature": na.AsString(staged["dealSignature"])}})
	}
	if err := stage("purchase-stage", []any{line(componentID, 1)}); err != nil {
		return err
	}
	result, err := accept("purchase-accept")
	if err != nil {
		return err
	}
	effect, err := applied("purchase-accept", result)
	if err != nil {
		return err
	}
	s.Report()["purchase"] = effect
	if err := closed("purchase-closed"); err != nil {
		return err
	}
	var after map[string]any
	// Orbital goods are incoming drop pods until vanilla releases their contents.
	observeDelivery := func(ctx context.Context) (string, bool, error) {
		var err error
		after, err = inventory("delivery-inventory")
		return fmt.Sprint(after), num(after["components"]) == 1, err
	}
	if _, ready, err := observeDelivery(ctx); err != nil {
		return err
	} else if !ready {
		if _, err := na.RunUntil(ctx, h, "native-delivery", 2500, na.Wait{Stall: na.StallBudget()}, observeDelivery); err != nil {
			return err
		}
	}
	s.Report()["before"], s.Report()["after"] = before, after
	if num(after["sellerComponents"]) != 19 || num(after["medicine"]) != 6 || num(after["silver"]) >= 2000 || num(after["silver"]) < 1000 || num(after["silver"]) != num(effect["afterSilver"]) {
		return fmt.Errorf("native purchase inventory/silver mismatch: %#v effect=%#v", after, effect)
	}
	if channel == "settlement" && num(after["homeComponents"]) != 0 {
		return fmt.Errorf("away purchase counted as home delivery: %#v", after)
	}
	return nil
}
