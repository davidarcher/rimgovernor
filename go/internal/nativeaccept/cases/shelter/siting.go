package shelter

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// shelter/siting (#2048): the first real-game read of the shelter room the
// layout plan carries at the start (#2041, #2044). The plan alone is the
// assertion, so the run stops after the first recorded plan.

func init() {
	cases.Register(cases.Case{
		Name: "shelter/siting",
		Scope: "Issue #2048: from the tribal " + sustained.BaselineSave + " baseline the first recorded layout plan holds exactly one " +
			"shelter room, its floor overlaps no other room's, and its interior holds at least ShelterInteriorArea cells for the " +
			"colony's colonists with the latched climate's campfire and cooler slots (cold: two campfires, hot: a passive cooler). " +
			"The siting gap to the nearest other room is reported, not asserted: the planner falls back to a smaller gap when " +
			"the preferred one fits nowhere.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Serve:  &cases.ServeSpec{Families: []string{"shelter", "sleeping"}, NativeTimeout: 30 * time.Second, Prefix: "shelter-siting"},
		Budget: 4 * time.Minute,
		Reason: "one short watch until the first review records a layout plan",
		Run:    siting,
	})
}

func siting(ctx context.Context, s cases.Session) error {
	report := s.Report()
	listed, err := na.ListColonists(ctx, s.Harness(), "colonists", false, false)
	if err != nil {
		return err
	}
	colonists := len(listed)
	if colonists == 0 {
		return fmt.Errorf("the baseline has no colonists")
	}
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	var record store.LayoutPlanRecord
	if _, err := service.WaitReview(ctx, na.Wait{Ceiling: 3 * time.Minute}, func(r store.Rounds) bool {
		got, ok, err := journal.LayoutPlan(ctx, r.Snapshot, r.Tick)
		if err != nil || !ok {
			return false
		}
		record = got
		return true
	}); err != nil {
		return fmt.Errorf("wait for the first layout plan: %w", err)
	}
	plan := record.Plan
	var shelters []policy.PlannedRoom
	for _, r := range plan.AllRooms() {
		if r.Role == policy.PlannedShelter {
			shelters = append(shelters, r)
		}
	}
	report["plan"] = map[string]any{"tick": record.Tick, "cold": plan.Cold, "hot": plan.Hot, "colonists": colonists, "shelters": len(shelters)}
	if len(shelters) != 1 {
		return fmt.Errorf("the plan holds %d shelter rooms, want 1", len(shelters))
	}
	shelter := shelters[0]
	in := shelter.Interior
	campfires, coolers := policy.ShelterCampfires(plan.Cold), policy.ShelterCoolers(plan.Hot)
	want := policy.ShelterInteriorArea(colonists, campfires, coolers)
	if got := int(in.Width * in.Height); got < want {
		return fmt.Errorf("shelter interior %dx%d holds %d cells, %d needed for %d colonists, %d campfires, %d coolers", in.Width, in.Height, got, want, colonists, campfires, coolers)
	}
	gap := int32(-1)
	for _, r := range plan.AllRooms() {
		if r.Role == policy.PlannedShelter {
			continue
		}
		o := r.Interior
		dx := max(o.X-(in.X+in.Width-1), in.X-(o.X+o.Width-1)) - 1
		dz := max(o.Z-(in.Z+in.Height-1), in.Z-(o.Z+o.Height-1)) - 1
		d := max(dx, dz)
		if d < 0 {
			return fmt.Errorf("shelter interior %+v overlaps %s room %+v", in, r.Role, o)
		}
		if gap < 0 || d < gap {
			gap = d
		}
	}
	report["shelter"] = map[string]any{"interior": in, "door": shelter.Door, "campfire_slots": campfires, "cooler_slots": coolers, "gap_to_nearest_room": gap}
	return nil
}
