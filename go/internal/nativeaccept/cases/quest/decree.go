package quest

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

func init() {
	cases.Register(cases.Case{
		Name: "quest/decree", Scope: "Real produce-item and harvest decree signal graphs, fulfilled by controller bill and growing-zone orders, normal pawn production/harvesting, and native quest Success. A Go snapshot cannot prove vanilla Notify_ThingsProduced/Notify_PlantHarvested completion. Hunt and monument decrees excluded.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_decree_prepare"}, Crew: cases.Crew{Size: 6}, Expansions: []string{"ludeon.rimworld.royalty"}, NoKeep: true, Quiet: na.QuietRequired, Budget: 10 * time.Minute,
		RequiredOps: []string{"test/quest_decree_prepare", "test/quest_lifecycle_read"},
		Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.PopulationJoiner, routinefamily.Field, routinefamily.Work, routinefamily.Bill, routinefamily.Supply, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "quest-decree"}, Run: runDecree,
	})
}

func runDecree(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	produce, harvest := na.AsString(prepared["produceQuest"]), na.AsString(prepared["harvestQuest"])
	if produce == "" || harvest == "" {
		return fmt.Errorf("invalid decree fixture: %#v", prepared)
	}
	for _, id := range []string{produce, harvest} {
		row, err := lifecycleQuest(ctx, s.Harness(), s.Identity(), id)
		if err != nil {
			return err
		}
		if na.AsString(row["state"]) != "QUEST_STATUS_ONGOING" {
			return fmt.Errorf("decree must begin unfinished and ongoing: %#v", row)
		}
		for _, raw := range na.AsSlice(row["objectives"]) {
			objective, _ := na.AsMap(raw)
			if na.AsNumber(objective["produced"]) > 0 {
				return fmt.Errorf("fixture supplied objective progress: %#v", objective)
			}
		}
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
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer st.Close()
	err = waitLifecycleOrders(ctx, service, st, func(plans []store.PlanState) bool {
		bill, zone := false, false
		completedLifecycleActions(plans, func(a domain.Action) {
			if v, ok := a.ProductionBill(); ok && v.Bench() == na.AsString(prepared["benchId"]) {
				bill = true
			}
			if _, ok := a.ZoneCreate(); ok {
				zone = true
			}
		})
		return bill && zone
	})
	if err != nil {
		return fmt.Errorf("decree bill and growing-zone orders: %w", err)
	}
	s.Report()["run_keepalive"] = service.Stop()
	if err = finishLifecycle(ctx, s, produce, harvest); err != nil {
		return err
	}
	read, err := s.Harness().Call(ctx, "decree-physical-proof", "test/quest_lifecycle_read", map[string]any{"questId": produce})
	if err != nil {
		return err
	}
	s.Report()["native_work"] = read
	if na.AsNumber(read["bills"]) < 1 || na.AsNumber(read["growingZones"]) < 1 || na.AsNumber(read["products"]) < 1 {
		return fmt.Errorf("native decree work missing: %#v", read)
	}
	return nil
}
