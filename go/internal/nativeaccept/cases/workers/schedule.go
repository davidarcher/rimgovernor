package workers

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name: "workers/nightowl",
		Scope: "Schedule planner (#417): a NightOwl on the native default timetable is written a night shift and a " +
			"QuickSleeper a six-hour sleep through the first native PatchPawn schedule write, beside the work rows " +
			"under the same snapshot token; a colonist whose timetable was edited by hand is replanned and rewritten too " +
			"(#461: provenance is not authority over fresh planning).",
		Start:       cases.DebugStart{},
		RequiredOps: []string{"test/workers_setup"},
		Budget:      4 * time.Minute,
		Run:         runNightOwl,
	})
}

func schedule(pawns []policy.WorkPawn, id policy.PawnID) []string {
	for _, p := range pawns {
		if p.ID == id {
			slots, _ := p.Schedule.Value()
			return slots
		}
	}
	return nil
}

func count(slots []string, def string) int {
	n := 0
	for _, s := range slots {
		if s == def {
			n++
		}
	}
	return n
}

func runNightOwl(ctx context.Context, s cases.Session) error {
	report := s.Report()
	if !na.Contains(s.Names(), "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}
	fixture, err := seed(ctx, s, "nightowl")
	if err != nil {
		return err
	}
	owl, quick, edited := policy.PawnID(fixture.IDs[0]), policy.PawnID(fixture.IDs[1]), policy.PawnID(fixture.IDs[2])
	pawns, err := readPawns(ctx, s, "before", fixture.IDs)
	if err != nil {
		return err
	}
	if before := schedule(pawns, edited); len(before) != 24 || before[12] != policy.ScheduleJoy {
		return fmt.Errorf("before: the player-edited timetable did not read back: %v", before)
	}
	planned := policy.PlanSchedules(pawns)
	report["schedules_before"] = planned
	wanted := map[policy.PawnID][]string{}
	for _, row := range planned.Schedules {
		if row.Matches {
			return fmt.Errorf("before: %s already matches its template", row.Pawn)
		}
		wanted[row.Pawn] = row.Slots
	}
	if len(wanted) != 3 || wanted[owl] == nil || wanted[quick] == nil || wanted[edited] == nil {
		return fmt.Errorf("before: expected the owl, the quick sleeper and the edited pawn planned, got %v", planned.Schedules)
	}
	if wanted[edited][12] != policy.ScheduleAnything {
		return fmt.Errorf("before: the edited pawn's template keeps the hand-written Joy hour: %v", wanted[edited])
	}
	if count(wanted[owl], policy.ScheduleWork) != 8 || wanted[owl][0] != policy.ScheduleWork || wanted[owl][12] != policy.ScheduleSleep {
		return fmt.Errorf("before: night owl template is not a night shift: %v", wanted[owl])
	}
	if count(wanted[quick], policy.ScheduleSleep) != 6 {
		return fmt.Errorf("before: quick sleeper template is not a six-hour sleep: %v", wanted[quick])
	}

	// The timetable rides the same PatchPawn as the work rows the flat
	// sheet needs, so the write below carries both fields for all three.
	decision, err := policy.PlanWork(pawns, nil, nil, policy.WorkDemand{})
	if err != nil {
		return err
	}
	if _, err := na.GrantAuto(ctx, s.Harness().WireFunc(), "acquire", s.Identity()); err != nil {
		return err
	}
	written, err := dispatch(ctx, s, "write", pawns, decision, wanted)
	if err != nil {
		return err
	}
	report["pawns_written"] = written

	after, err := readPawns(ctx, s, "after", fixture.IDs)
	if err != nil {
		return err
	}
	for id, slots := range wanted {
		got := schedule(after, id)
		for h := range slots {
			if got[h] != slots[h] {
				return fmt.Errorf("after: %s hour %d reads %s, want %s", id, h, got[h], slots[h])
			}
		}
	}
	replan := policy.PlanSchedules(after)
	report["schedules_after"] = replan
	if len(replan.Schedules) != 3 {
		return fmt.Errorf("after: expected all three pawns planned, got %v", replan.Schedules)
	}
	for _, row := range replan.Schedules {
		if !row.Matches {
			return fmt.Errorf("after: %s still differs from its template", row.Pawn)
		}
	}
	work, err := policy.PlanWork(after, nil, nil, policy.WorkDemand{})
	if err != nil {
		return err
	}
	if matches, _ := work.Matches.Value(); !matches {
		return fmt.Errorf("after: the work rows written beside the timetables do not match: %+v", work.Assignments)
	}
	return nil
}
