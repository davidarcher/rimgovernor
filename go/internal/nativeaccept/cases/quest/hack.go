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
		Name: "quest/hack", Scope: "Native autohack enable/disable, replay and unhackable-target refusal, followed by ordinary Research WorkGiver hacking an Ideology terminal. A Go snapshot cannot prove vanilla pawn work, CompHackable completion or the exact native hacker.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_hack_prepare"}, NoKeep: true, Crew: cases.Crew{Size: 3}, Expansions: []string{"ludeon.rimworld.ideology"}, Quiet: na.QuietRequired, Budget: 5 * time.Minute,
		RequiredOps: []string{"test/quest_hack_prepare", "test/quest_hack_read"}, Run: runHack,
	})
}

func runHack(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	terminal, hacker, unavailable := na.AsString(prepared["terminalId"]), na.AsString(prepared["hackerId"]), na.AsString(prepared["unhackableItemId"])
	if terminal == "" || hacker == "" || unavailable == "" {
		return fmt.Errorf("invalid hack fixture: %#v", prepared)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	apply := func(label, key, target string, enabled bool) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "hackDesignation": map[string]any{"target": map[string]any{"id": target}, "enabled": enabled}}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: missing result: %#v", label, reply)
		}
		row, _ := na.AsMap(results[0])
		return row, nil
	}
	read := func(label string) (map[string]any, error) {
		return h.Call(ctx, label, "test/quest_hack_read", map[string]any{"terminalId": terminal})
	}
	assertApplied := func(row map[string]any, err error) error {
		if err != nil {
			return err
		}
		if applied, _ := na.AsMap(row["applied"]); applied == nil {
			return fmt.Errorf("hack action refused: %#v", row)
		}
		return nil
	}
	row, err := read("hack-before")
	if err != nil {
		return err
	}
	if hacked, _ := na.AsBool(row["hacked"]); hacked || na.AsNumber(row["progress"]) != 0 {
		return fmt.Errorf("fixture supplied completion: %#v", row)
	}
	result, err := apply("unhackable", "hack-unavailable", unavailable, true)
	if err != nil {
		return err
	}
	refused, _ := na.AsMap(result["refused"])
	if refused == nil || na.AsString(refused["code"]) != "FAILURE_CODE_UNAVAILABLE" {
		return fmt.Errorf("unhackable target accepted: %#v", result)
	}
	if err = assertApplied(apply("enable", "hack-enable", terminal, true)); err != nil {
		return err
	}
	if err = assertApplied(apply("enable-replay", "hack-enable", terminal, true)); err != nil {
		return err
	}
	row, err = read("hack-enabled")
	if err != nil {
		return err
	}
	if enabled, _ := na.AsBool(row["autohack"]); !enabled {
		return fmt.Errorf("enable replay toggled off: %#v", row)
	}
	if err = assertApplied(apply("disable", "hack-disable", terminal, false)); err != nil {
		return err
	}
	row, err = read("hack-disabled")
	if err != nil {
		return err
	}
	if enabled, _ := na.AsBool(row["autohack"]); enabled {
		return fmt.Errorf("disable did not apply: %#v", row)
	}
	if err = assertApplied(apply("work-enable", "hack-work", terminal, true)); err != nil {
		return err
	}
	tick := uint64(na.AsNumber(row["tick"]))
	return na.WaitProgress(ctx, na.Wait{Ceiling: 3 * time.Minute, Stall: na.StallBudget(), Ticks: 6000, Tick: func(context.Context) (uint64, error) { return tick, nil }}, func(ctx context.Context) (string, bool, error) {
		row, err := read("hack-work-read")
		if err != nil {
			return "", false, err
		}
		tick = uint64(na.AsNumber(row["tick"]))
		if hacked, _ := na.AsBool(row["hacked"]); hacked {
			if na.AsString(row["hackerId"]) != hacker {
				return "", false, fmt.Errorf("completion lacks exact ordinary hacker: %#v", row)
			}
			return na.Signature(row), true, nil
		}
		if _, err = s.Advance(ctx, 300); err != nil {
			return "", false, err
		}
		return na.Signature(row), false, nil
	})
}
