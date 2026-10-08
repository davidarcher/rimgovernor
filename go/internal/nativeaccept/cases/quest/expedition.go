package quest

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
)

func init() {
	for _, mission := range []bool{false, true} {
		name := "quest/expedition"
		var expansions []string
		if mission {
			name = "quest/bandit-camp"
			expansions = []string{"ludeon.rimworld.royalty"}
		}
		cases.Register(cases.Case{Name: name,
			Scope: "Real adjacent bandit quest round trip: accept or follow, staff, form/board, clear the generated site and return living travelers with identifiable loot. The fixture stages 30 silver on the site when its native map first generates. Go snapshots cannot prove transport, vanilla combat quest signals, physical home arrival or loot extraction.",
			Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_expedition_prepare", Args: map[string]any{"shuttleMission": mission}}, Crew: cases.Crew{Size: 6}, Expansions: expansions, NoKeep: true, Quiet: na.QuietRequired, Budget: 12 * time.Minute,
			RequiredOps: []string{"test/quest_expedition_prepare", "test/quest_expedition_read"},
			Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.PopulationJoiner, routinefamily.Defense, routinefamily.Work, routinefamily.Supply, routinefamily.Sleeping, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "quest-expedition"}, Run: runExpedition,
		})
	}
}

func runExpedition(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	id := na.AsString(prepared["questId"])
	mission, _ := na.AsBool(prepared["shuttleMission"])
	if id == "" || na.AsString(prepared["siteId"]) == "" {
		return fmt.Errorf("invalid expedition fixture: %#v", prepared)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	// Keep the governor attached until the original native roster leaves and
	// returns. The final witness also proves site combat, transport and loot.
	left := false
	tick := func(context.Context) (uint64, error) {
		row, err := service.Get("GET", "/api/player/colony")
		return uint64(na.AsNumber(row["tick"])), err
	}
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 8 * time.Minute, Stall: na.StallBudget(), Terminal: service.Exited, Ticks: 30000, Tick: tick}, func(context.Context) (string, bool, error) {
		row, err := service.Get("GET", "/api/player/colony")
		if err != nil {
			return "", false, err
		}
		present := map[string]bool{}
		for _, raw := range na.AsSlice(row["pawns"]) {
			pawn, _ := na.AsMap(raw)
			present[na.AsString(pawn["id"])] = true
		}
		allHome := true
		for _, raw := range na.AsSlice(prepared["pawnIds"]) {
			if !present[na.AsString(raw)] {
				allHome = false
			}
		}
		left = left || !allHome
		return na.Signature(row["tick"], row["pawns"]), left && allHome, nil
	})
	if err != nil {
		return fmt.Errorf("expedition outbound and return lifecycle: %w", err)
	}
	s.Report()["run_keepalive"] = service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	elapsed, err := na.RunUntil(ctx, h, "quest-expedition-physical-round-trip", 30000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		read, err := h.Call(ctx, "expedition-physical-proof", "test/quest_expedition_read", map[string]any{"questId": id})
		if err != nil {
			return "", false, err
		}
		s.Report()["native_expedition"] = read
		for _, field := range []string{"arrived", "cleared", "returned", "lootReturned"} {
			if value, _ := na.AsBool(read[field]); !value {
				return na.Signature(read), false, nil
			}
		}
		transport := "formed"
		if mission {
			transport = "boarded"
		}
		if value, _ := na.AsBool(read[transport]); !value {
			return na.Signature(read), false, nil
		}
		if na.AsString(read["state"]) != "EndedSuccess" || na.AsNumber(read["loot"]) < 30 {
			return na.Signature(read), false, nil
		}
		return na.Signature(read), true, nil
	})
	s.Report()["native_round_trip_ticks"] = elapsed
	return err
}
