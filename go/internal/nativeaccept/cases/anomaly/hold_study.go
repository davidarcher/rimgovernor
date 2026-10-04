package anomaly

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	holdPrepareTool = "test/entity_hold_prepare"
	holdInspectTool = "test/entity_hold_inspect"
	// holdCeiling bounds the journal wait in wall time; the stall budget
	// ends it earlier when nothing moves.
	holdCeiling = 8 * time.Minute
	// studyTicks bounds the native run after the service stops: the carried
	// entity is chained to the platform and a colonist walks to it and works
	// one DarkStudy interaction (a few thousand ticks).
	studyTicks = 3 * na.TicksPerDay
)

func init() {
	cases.Register(cases.Case{
		Name: "anomaly/hold-study",
		Scope: "Issue #1747: a colony with a finished containment cell captures a downed, capturable entity, holds it on the " +
			"platform and studies it: the capture rule (#1742) takes the entity to the platform, the work planner owes a DarkStudy " +
			"owner while the held entity is currently studiable (#1744), and natively the entity is alive, chained on the platform " +
			"and has had a study interaction worked on it. A Go snapshot over recorded facts cannot cover it: containment strength, " +
			"the carry to the platform, the chains and the study job and its interval are vanilla's, asserted on the live pawn " +
			"and its CompStudiable; the rule and the planner's decisions are policy/entity_capture_test.go and entity_study_test.go. " +
			"The fixture picks the entity and the platform from the game's defs and builds the cell finished (its planning is " +
			"#1741's, policy tests).",
		Start:       cases.Fixture{Op: holdPrepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.anomaly"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{holdPrepareTool, holdInspectTool},
		// population-custody captures and keeps the held entity, work owes the
		// DarkStudy owner and tend answers a captive's wounds; the shelter
		// family keeps supervised windows running and dialog answers letters.
		Serve: &cases.ServeSpec{
			Families: []string{"population-custody", "work", "tend", "shelter", "dialog"}, NativeTimeout: 15 * time.Second, Prefix: "anomaly-hold-study",
		},
		Budget: 12 * time.Minute,
		Run:    runHoldStudy,
	})
}

// holdProgress is what the journal shows of the colony's answer.
type holdProgress struct {
	// captured is a completed Capture of the entity; studyOwner a completed
	// work assignment that gives a colonist a DarkStudy priority.
	captured, studyOwner bool
}

func journalHold(ctx context.Context, st *store.Store, entity domain.PawnID) (holdProgress, error) {
	var out holdProgress
	captures, err := st.PlanHistoryWithMethods(ctx, 256, "population-capture-*")
	if err != nil {
		return out, err
	}
	for _, p := range captures {
		for _, progress := range p.Progress {
			capture, ok := progress.Action().Capture()
			if ok && capture.Patient() == entity && progress.View().Stage == domain.Completed {
				out.captured = true
			}
		}
	}
	works, err := st.PlanHistoryWithMethods(ctx, 256, "work-*")
	if err != nil {
		return out, err
	}
	for _, p := range works {
		for _, progress := range p.Progress {
			work, ok := progress.Action().WorkAssignment()
			if !ok || progress.View().Stage != domain.Completed {
				continue
			}
			for _, setting := range work.Settings() {
				if setting.Definition == string(policy.WorkDarkStudy) && setting.Priority > 0 {
					out.studyOwner = true
				}
			}
		}
	}
	return out, nil
}

func runHoldStudy(ctx context.Context, s cases.Session) error {
	report, h, prepared := s.Report(), s.Harness(), s.Prepared()
	entity, platform := na.AsString(prepared["entityId"]), na.AsString(prepared["platformId"])
	if entity == "" || platform == "" || na.AsNumber(prepared["platformStrength"]) <= na.AsNumber(prepared["need"]) {
		return fmt.Errorf("fixture: unexpected entity hold staging: %#v", prepared)
	}
	report["fixture"] = prepared

	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
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

	// The colony's answer is journalled: the capture order for the entity,
	// then the DarkStudy owner the work planner owes once the entity is held
	// and currently studiable. The native outcome is read after the service
	// releases the game slot.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: holdCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		got, err := journalHold(ctx, st, domain.PawnID(entity))
		if err != nil {
			return "", false, err
		}
		report["captured"], report["study_owner"] = got.captured, got.studyOwner
		return na.Signature(got.captured, got.studyOwner), got.captured && got.studyOwner, nil
	})
	if err != nil {
		return fmt.Errorf("capture and study owner: %w", err)
	}
	report["run_keepalive"] = service.Stop()

	// Native postconditions: the entity is alive and chained on the platform
	// and a colonist has worked a study interaction on it.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	var last map[string]any
	elapsed, err := na.RunUntil(ctx, h, "entity-studied", studyTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Call(ctx, "hold-inspect", holdInspectTool, map[string]any{"entityId": entity, "platformId": platform})
		if err != nil {
			return "", false, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return "", false, fmt.Errorf("%s refused: %#v", holdInspectTool, reply)
		}
		last = reply
		if found, _ := na.AsBool(reply["found"]); !found {
			return "", false, fmt.Errorf("the entity %s is gone: %#v", entity, reply)
		}
		if dead, _ := na.AsBool(reply["dead"]); dead {
			return "", false, fmt.Errorf("the entity %s died: %#v", entity, reply)
		}
		held, _ := na.AsBool(reply["held"])
		return na.Signature(held, na.AsNumber(reply["studyInteractions"])), held && na.AsNumber(reply["studyInteractions"]) >= 1, nil
	})
	report["study_ticks"], report["inspect"] = elapsed, last
	if err != nil {
		return fmt.Errorf("study: %w (last read %s)", err, strings.TrimSpace(fmt.Sprint(last)))
	}
	if onPlatform, _ := na.AsBool(last["onPlatform"]); !onPlatform {
		return fmt.Errorf("the entity %s is held but not on the staged platform %s: %#v", entity, platform, last)
	}
	if escaping, _ := na.AsBool(last["escaping"]); escaping {
		return fmt.Errorf("the entity %s is escaping: %#v", entity, last)
	}
	return nil
}
