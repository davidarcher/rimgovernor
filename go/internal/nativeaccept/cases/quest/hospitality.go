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
		Name: "quest/hospitality", Scope: "Real Hospitality_Joiners offer with a titled lodger: native bedroom requirement, hosted guest bed assignment, pickup boarding and vanilla Success through world progression. A Go snapshot cannot prove arrival, bed ownership, transporter boarding or the vanilla end signal.",
		Start: cases.Fixture{On: cases.LabStart(), Op: "test/quest_hospitality_prepare"}, Crew: cases.Crew{Size: 6}, Expansions: []string{"ludeon.rimworld.royalty"}, NoKeep: true, Quiet: na.QuietRequired, Budget: 12 * time.Minute,
		RequiredOps: []string{"test/quest_hospitality_prepare", "test/quest_lifecycle_read"},
		Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.PopulationJoiner, routinefamily.Sleeping, routinefamily.Shelter, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "quest-hospitality"}, Run: runHospitality,
	})
}

func runHospitality(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	id, pawn, bed := na.AsString(prepared["questId"]), na.AsString(prepared["pawnId"]), na.AsString(prepared["bedId"])
	if id == "" || pawn == "" || bed == "" || na.AsString(prepared["title"]) == "" {
		return fmt.Errorf("invalid hospitality fixture: %#v", prepared)
	}
	before, err := lifecycleQuest(ctx, s.Harness(), s.Identity(), id)
	if err != nil {
		return err
	}
	can, _ := na.AsBool(before["canAccept"])
	if !can || na.AsString(before["state"]) != "QUEST_STATUS_NOT_YET_ACCEPTED" || na.AsString(before["scriptDef"]) != "Hospitality_Joiners" {
		return fmt.Errorf("hospitality precondition: %#v", before)
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
		assigned, launched := false, false
		completedLifecycleActions(plans, func(a domain.Action) {
			if v, ok := a.Assign(); ok && string(v.Pawn()) == pawn && v.Thing() == bed {
				assigned = true
			}
			if v, ok := a.QuestShuttle(); ok && string(v.Quest()) == id && v.Launch() {
				launched = true
			}
		})
		return assigned && launched
	})
	if err != nil {
		return fmt.Errorf("guest bed and pickup orders: %w", err)
	}
	s.Report()["run_keepalive"] = service.Stop()
	if err = finishLifecycle(ctx, s, id); err != nil {
		return err
	}
	read, err := s.Harness().Call(ctx, "hospitality-physical-proof", "test/quest_lifecycle_read", map[string]any{"questId": id})
	if err != nil {
		return err
	}
	s.Report()["native_hosting"] = read
	for _, key := range []string{"hosted", "bedAssigned", "boarded"} {
		value, _ := na.AsBool(read[key])
		if !value {
			return fmt.Errorf("native hospitality %s not observed: %#v", key, read)
		}
	}
	return nil
}
