package quest

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{Name: "quest/shuttle", Scope: "Native autoload toggle, explicit pawn boarding and loaded shuttle launch. A Go snapshot cannot cover vanilla EnterTransporter work and transport-ship departure; actual passenger containers and spawned state are asserted.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_shuttle_prepare"}, NoKeep: true, Crew: cases.Crew{Size: 3}, Expansions: []string{"ludeon.rimworld.royalty"},
		Quiet: na.QuietRequired, Budget: 5 * time.Minute, RequiredOps: []string{"test/quest_shuttle_prepare", "test/quest_shuttle_read"}, Run: runShuttle})
}

func runShuttle(ctx context.Context, s cases.Session) error {
	h, id, prepared := s.Harness(), s.Identity(), s.Prepared()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", id); err != nil {
		return err
	}
	apply := func(label string, intent map[string]any) error {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": id, "actions": []any{map[string]any{"key": label, "questShuttle": intent}}})
		if err != nil {
			return err
		}
		rows := na.AsSlice(reply["results"])
		if len(rows) != 1 {
			return fmt.Errorf("%s: missing action result: %#v", label, reply)
		}
		row, _ := na.AsMap(rows[0])
		receipt, _ := na.AsMap(row["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		if applied == nil {
			return fmt.Errorf("%s: shuttle action not applied: %#v", label, row)
		}
		return nil
	}
	read := func(label, quest string) (map[string]any, error) {
		return h.Call(ctx, label, "test/quest_shuttle_read", map[string]any{"questId": quest})
	}
	for _, tc := range []struct {
		prefix, quest, pawn string
		auto                bool
	}{
		{"named", na.AsString(prepared["namedQuest"]), na.AsString(prepared["namedPawn"]), true},
		{"counted", na.AsString(prepared["countedQuest"]), na.AsString(prepared["countedPawn"]), false},
	} {
		if tc.quest == "" || tc.pawn == "" {
			return fmt.Errorf("missing fixture ids: %#v", prepared)
		}
		intent := map[string]any{"questId": tc.quest}
		if tc.auto {
			intent["autoload"] = true
		} else {
			intent["explicitPawns"] = map[string]any{"pawnIds": []string{tc.pawn}}
		}
		if err := apply(tc.prefix+"-load", intent); err != nil {
			return err
		}
		loaded := false
		for advanced := 0; advanced <= 6000; advanced += 300 {
			row, err := read(fmt.Sprintf("%s-board-%d", tc.prefix, advanced), tc.quest)
			if err != nil {
				return err
			}
			if value, _ := na.AsBool(row["loaded"]); value {
				found := false
				for _, pawn := range na.AsSlice(row["pawnIds"]) {
					found = found || na.AsString(pawn) == tc.pawn
				}
				if !found {
					return fmt.Errorf("%s: loaded without exact pawn: %#v", tc.prefix, row)
				}
				loaded = true
				break
			}
			if advanced == 6000 {
				break
			}
			if _, err := s.Advance(ctx, 300); err != nil {
				return err
			}
		}
		if !loaded {
			return fmt.Errorf("%s: boarding did not complete within 6000 ticks", tc.prefix)
		}
		if err := apply(tc.prefix+"-launch", map[string]any{"questId": tc.quest, "launch": true}); err != nil {
			return err
		}
		departed := false
		for advanced := 0; advanced <= 3000; advanced += 300 {
			row, err := read(fmt.Sprintf("%s-depart-%d", tc.prefix, advanced), tc.quest)
			if err != nil {
				return err
			}
			if spawned, known := na.AsBool(row["spawned"]); known && !spawned {
				departed = true
				break
			}
			if advanced == 3000 {
				break
			}
			if _, err := s.Advance(ctx, 300); err != nil {
				return err
			}
		}
		if !departed {
			return fmt.Errorf("%s: shuttle did not depart", tc.prefix)
		}
	}
	return nil
}
