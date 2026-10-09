// Package ideology holds the Ideology acceptance cases.
//
// The ideology/first-ritual case proves the colony holds a ritual of
// its ideoligion on its own: MaintainRituals finds the staged ritual
// due, plans its organizer and attendees at the finished building and commits
// the Ritual `begin`; native runs the begin dialog's confirm action,
// and the game's own lord job holds the ritual to its end.
package ideology

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	ritualPrepareTool = "test/first_ritual_prepare"
	ritualInspectTool = "test/first_ritual_inspect"
	// ritualCeiling bounds the journal wait; the stall budget, whose
	// signature carries the game day, ends it earlier when nothing moves.
	ritualCeiling = 10 * time.Minute
	// ritualTicks bounds the native run after the begin: attendees walk to
	// the building and the lord job runs the ritual to its end.
	ritualTicks = 30000
)

func init() {
	cases.Register(cases.Case{
		Name: "ideology/first-ritual",
		Scope: "Issue #1665: a colony whose ideoligion has a free-start ritual and its finished building plans the ritual " +
			"through MaintainRituals, begins it through the Ritual begin verb, and the game's lord job of that ritual " +
			"precept runs to its end and advances the precept's last finished tick. A Go snapshot over recorded facts " +
			"cannot cover it: the start and the outcome are vanilla's own (the begin dialog's confirm action and role " +
			"assignment, attendees' availability, the lord job and its toils, the ritual's completion), asserted on the " +
			"live precept; the planner's decisions are policy/ritual_test.go. The fixture picks the ritual and its " +
			"building from the game's defs and places the building finished (the worship room's construction is " +
			"#1658's); it passes the ritual's cooldown.",
		Start:       cases.Fixture{Op: ritualPrepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.ideology"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{ritualPrepareTool, ritualInspectTool},
		// rituals plans the begin and ideo-roles fills the role precepts its
		// slots name; the shelter family keeps supervised windows running (the
		// planners alone never advance the clock) and dialog answers letters.
		Serve: &cases.ServeSpec{
			Families: []routinefamily.Family{routinefamily.Rituals, routinefamily.IdeoRoles, routinefamily.Shelter, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "ideology-first-ritual",
		},
		Budget: 15 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runFirstRitual,
	})
}

// beginStages returns the stage of each journalled begin of the ritual
// precept, newest plan first as the journal lists them.
func beginStages(ctx context.Context, st *store.Store, ritual string) ([]domain.Stage, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "ritual-*")
	if err != nil {
		return nil, err
	}
	var stages []domain.Stage
	for _, p := range plans {
		for _, progress := range p.Progress {
			begin, ok := progress.Action().Ritual()
			if ok && begin.Verb() == domain.RitualBegin && string(begin.Ritual()) == ritual {
				stages = append(stages, progress.View().Stage)
			}
		}
	}
	return stages, nil
}

func completed(stages []domain.Stage) bool {
	for _, stage := range stages {
		if stage == domain.Completed {
			return true
		}
	}
	return false
}

func runFirstRitual(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	var h *na.Harness
	ritual, building := na.AsString(prepared["ritualId"]), na.AsString(prepared["buildingId"])
	baseline := na.AsNumber(prepared["lastFinishedTick"])
	if ritual == "" || building == "" || len(na.AsSlice(prepared["pawnIds"])) < 2 {
		return fmt.Errorf("fixture: unexpected first-ritual staging: %#v", prepared)
	}
	report["ritual"], report["fixture"] = ritual, prepared

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer st.Close()

	// The colony's answer to a ritual that is due: one Ritual begin the game
	// accepted. The signature carries the game day so a long wait for the
	// attendees does not read as a stall and a broken planner does.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: ritualCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		stages, err := beginStages(ctx, st, ritual)
		if err != nil {
			return "", false, err
		}
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", stages), false, nil
		}
		report["begin_stages"] = stages
		return na.Signature(stages, review.Tick/na.TicksPerDay), completed(stages), nil
	})
	if err != nil {
		return fmt.Errorf("ritual begin: %w", err)
	}
	report["run_keepalive"] = service.Stop()

	// Native postcondition after the service releases the game slot: the
	// ritual's lord job runs on and the precept's last finished tick advances
	// past the staged baseline.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	var running bool
	var finished float64
	var participants float64
	sawRunning := false
	elapsed, err := na.RunUntil(ctx, h, "ritual-finished", ritualTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Call(ctx, "ritual-inspect", ritualInspectTool, map[string]any{"ritualId": ritual})
		if err != nil {
			return "", false, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return "", false, fmt.Errorf("ritual inspect refused: %#v", reply)
		}
		running, _ = na.AsBool(reply["running"])
		finished = na.AsNumber(reply["lastFinishedTick"])
		if running {
			sawRunning = true
			participants = na.AsNumber(reply["participants"])
		}
		return na.Signature(running, finished, participants), finished > baseline, nil
	})
	report["ritual_ticks"], report["saw_running"], report["participants"], report["last_finished_tick"] = elapsed, sawRunning, participants, finished
	if err != nil {
		return err
	}
	if finished <= baseline {
		return fmt.Errorf("ritual %s last finished tick %v did not advance past %v", ritual, finished, baseline)
	}
	return nil
}
