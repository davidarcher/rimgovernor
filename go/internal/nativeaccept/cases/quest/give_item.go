package quest

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "quest/give-item", Scope: "Native whole-request count fencing and ordinary GiveToPawn delivery to an Ideology beggar. Go snapshots cannot prove vanilla hauling transfers the requested items into the exact visitor's inventory.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_give_prepare"}, NoKeep: true,
		Crew: cases.Crew{Size: 3}, Expansions: []string{"ludeon.rimworld.ideology"}, Quiet: na.QuietRequired, Budget: 3 * time.Minute,
		RequiredOps: []string{"test/quest_give_prepare", "test/quest_give_read"}, Run: runGiveItem,
	})
}

func runGiveItem(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	hauler, recipient := na.AsString(prepared["haulerId"]), na.AsString(prepared["recipientId"])
	if hauler == "" || recipient == "" || na.AsNumber(prepared["remaining"]) != 20 {
		return fmt.Errorf("invalid giving fixture: %#v", prepared)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	apply := func(key string, remaining int) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "giveItem": map[string]any{"hauler": map[string]any{"id": hauler}, "recipient": map[string]any{"id": recipient}, "definition": "Silver", "expectedRemaining": remaining}}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("missing giving result: %#v", reply)
		}
		row, _ := na.AsMap(results[0])
		return row, nil
	}
	read := func() (map[string]any, error) {
		return h.Call(ctx, "giving-read", "test/quest_give_read", map[string]any{"recipientId": recipient, "haulerId": hauler})
	}
	result, err := apply("giving-stale-count", 19)
	if err != nil {
		return err
	}
	if refused, _ := na.AsMap(result["refused"]); refused == nil || na.AsString(refused["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("changed native request count accepted: %#v", result)
	}
	before, err := read()
	if err != nil {
		return err
	}
	if na.AsNumber(before["received"]) != 0 || na.AsNumber(before["remaining"]) != 20 || na.AsString(before["job"]) == "GiveToPawn" {
		return fmt.Errorf("refusal changed native delivery state: %#v", before)
	}
	result, err = apply("giving-whole-request", 20)
	if err != nil {
		return err
	}
	if applied, _ := na.AsMap(result["applied"]); applied == nil {
		return fmt.Errorf("whole request refused: %#v", result)
	}
	started, err := read()
	if err != nil {
		return err
	}
	if na.AsString(started["job"]) != "GiveToPawn" || na.AsString(started["targetId"]) != recipient {
		return fmt.Errorf("exact vanilla delivery job missing: %#v", started)
	}
	tick := uint64(na.AsNumber(started["tick"]))
	return na.WaitProgress(ctx, na.Wait{Ceiling: 2 * time.Minute, Stall: na.StallBudget(), Ticks: 3000, Tick: func(context.Context) (uint64, error) { return tick, nil }}, func(ctx context.Context) (string, bool, error) {
		row, err := read()
		if err != nil {
			return "", false, err
		}
		tick = uint64(na.AsNumber(row["tick"]))
		if na.AsNumber(row["remaining"]) == 0 && na.AsNumber(row["received"]) == 20 {
			return na.Signature(row["received"], row["remaining"]), true, nil
		}
		if _, err = s.Advance(ctx, 60); err != nil {
			return "", false, err
		}
		return na.Signature(row["received"], row["remaining"], row["job"]), false, nil
	})
}
