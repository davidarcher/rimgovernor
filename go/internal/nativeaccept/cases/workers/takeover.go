package workers

import (
	"context"
	"fmt"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/takeover"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name: "takeover/schedule", Scope: "Manual player timetable edit is corrected by Auto's routine worker, with native readback and no provenance hold.",
		Start: cases.Save{Name: "RimGovernor-tribal8-baseline"}, QuietWorld: true, RequiredOps: []string{"test/workers_setup"},
		Serve:  &cases.ServeSpec{Families: []string{"work"}, Prefix: "takeover-schedule"},
		Budget: 3 * time.Minute, Run: runScheduleTakeover,
	})
}

func runScheduleTakeover(ctx context.Context, s cases.Session) error {
	if err := takeover.Manual(ctx, s); err != nil {
		return err
	}
	fixture, err := seed(ctx, s, "nightowl")
	if err != nil {
		return err
	}
	before, err := readPawns(ctx, s, "manual-edit", fixture.IDs)
	if err != nil {
		return err
	}
	edited := policy.PawnID(fixture.IDs[2])
	if slots := schedule(before, edited); len(slots) != 24 || slots[12] != policy.ScheduleJoy {
		return fmt.Errorf("player timetable edit missing: %v", slots)
	}
	wanted := policy.PlanSchedules(before)
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err = service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	err = na.WaitProgress(ctx, na.Wait{Ceiling: 90 * time.Second, Stall: 45 * time.Second, Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.PlanHistoryWithPrefix(ctx, "routine-work-", 256)
		if err != nil {
			return "", false, err
		}
		completed := map[string]bool{}
		for _, plan := range plans {
			for i, action := range plan.Spec.Actions() {
				assignment, ok := action.WorkAssignment()
				// A drug write goes out alone (#685); only the timetable write proves the edit.
				if ok && len(assignment.Schedule()) != 0 && slices.Contains(fixture.IDs, string(assignment.Pawn())) && plan.Progress[i].View().Stage == domain.Completed {
					completed[string(assignment.Pawn())] = true
				}
			}
		}
		return na.Signature(completed), len(completed) == len(fixture.IDs), nil
	})
	if err != nil {
		return err
	}
	if err = takeover.Journal(ctx, journal, s.Report()); err != nil {
		return err
	}
	service.Stop()
	if _, err = s.Reattach(ctx); err != nil {
		return err
	}
	after, err := readPawns(ctx, s, "auto-schedule-readback", fixture.IDs)
	if err != nil {
		return err
	}
	for _, row := range wanted.Schedules {
		if !slices.Equal(schedule(after, row.Pawn), row.Slots) {
			return fmt.Errorf("auto did not restore %s timetable", row.Pawn)
		}
	}
	s.Report()["schedules_after"] = policy.PlanSchedules(after)
	return nil
}
