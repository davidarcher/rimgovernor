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
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	monolithPrepareTool = "test/monolith_awaken_prepare"
	monolithInspectTool = "test/monolith_awaken_inspect"
	// monolithCeiling bounds the journal wait in wall time (under the case
	// budget); the stall budget ends it earlier when nothing moves.
	monolithCeiling = 13 * time.Minute
	// armedMargin is the slack the staged weapons must clear the awaken gate
	// by, so the case fails at once on a fixture that cannot pass the gate.
	armedMargin = 1.5
	// minInteractTargets is the least the quest can offer: one void structure,
	// the Gleaming monolith and the void node.
	minInteractTargets = 3
)

func init() {
	cases.Register(cases.Case{
		Name: "anomaly/monolith-disrupt",
		Scope: "Issue #2439: a colony with a void monolith at the Waking level, the codex entries the awakening needs and armed colonists " +
			"awakens the monolith (#2437, the awaken gate of policy/monolith.go), activates the void structures, uses the Gleaming monolith and " +
			"disrupts at the void node (#2438: the VoidNodeDisrupt dialog answer, never the embrace). The journal shows the awakening order, " +
			"the interactions and a dialog answered after the last of them; natively the monolith is at the Disrupted level, the questline has " +
			"ended and every colonist is alive. A Go snapshot over recorded facts cannot cover it: the awakening quest, its waves, the " +
			"structures, the pocket map the Gleaming monolith skips a colonist into and the node's dialog are vanilla's, asserted on the live " +
			"game; the rule and the dialog choice are policy/monolith_test.go and dialog_answer_test.go. The fixture sets the level and the " +
			"codex through the game's own GameComponent_Anomaly and EntityCodex (no entity, category, weapon or level literal is listed).",
		Start:       cases.Fixture{Op: monolithPrepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.anomaly"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{monolithPrepareTool, monolithInspectTool},
		// population-custody carries the monolith advance, defense answers the
		// waves, tend and rescue the casualties, the shelter family keeps
		// supervised windows running and dialog answers the confirmation, the
		// letters and the node's choice.
		Serve: &cases.ServeSpec{
			Families:      []routinefamily.Family{routinefamily.PopulationCustody, routinefamily.Defense, routinefamily.Tend, routinefamily.Rescue, routinefamily.Shelter, routinefamily.Dialog},
			NativeTimeout: 15 * time.Second, Prefix: "anomaly-monolith-disrupt",
		},
		Budget: 15 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runMonolithDisrupt,
	})
}

// monolithProgress is what the journal shows of the colony's answer.
type monolithProgress struct {
	// awakened is a completed ActivateMonolith order (from Waking the only
	// next level is the awakening); interacts the targets of completed
	// interact orders; windows the dialog windows answered, completed.
	awakened  bool
	interacts map[string]bool
	windows   map[int32]bool
}

func journalMonolith(ctx context.Context, st *store.Store) (monolithProgress, error) {
	out := monolithProgress{interacts: map[string]bool{}, windows: map[int32]bool{}}
	orders, err := st.PlanHistoryWithMethods(ctx, 256, "population-monolith-*")
	if err != nil {
		return out, err
	}
	for _, p := range orders {
		for _, progress := range p.Progress {
			service, ok := progress.Action().RecoveryService()
			if !ok || progress.View().Stage != domain.Completed {
				continue
			}
			switch service.Method() {
			case domain.RecoveryServiceActivateMonolith:
				out.awakened = true
			case domain.RecoveryServiceInteract:
				out.interacts[service.Thing()] = true
			}
		}
	}
	review, err := st.LoadRounds(ctx)
	if err != nil {
		return out, err
	}
	history, err := st.IncidentHistory(ctx, store.World{Colony: review.Snapshot.Colony, Load: review.Snapshot.Load, Map: review.Snapshot.Map}, policy.AnswerDialog)
	if err != nil {
		return out, err
	}
	for _, incident := range history {
		for _, m := range incident.Methods {
			plan, err := st.LoadPlan(ctx, m.Plan)
			if err != nil {
				return out, err
			}
			for i, action := range plan.Spec.Actions() {
				if answer, ok := action.DialogAnswer(); ok && plan.Progress[i].View().Stage == domain.Completed {
					out.windows[answer.WindowID()] = true
				}
			}
		}
	}
	return out, nil
}

func runMonolithDisrupt(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	var h *na.Harness
	colonists := idList(prepared["colonistIds"])
	capacity := policy.DefensePointsPerDPS * na.AsNumber(prepared["weaponDps"]) * na.AsNumber(prepared["armed"])
	need := policy.AwakenStrengthFactor * na.AsNumber(prepared["raidPoints"])
	if na.AsString(prepared["monolithId"]) == "" || na.AsString(prepared["levelDef"]) != "Waking" || len(colonists) == 0 || capacity < armedMargin*need {
		return fmt.Errorf("fixture: unexpected monolith staging (staged defense %.0f, awakening gate %.0f): %#v", capacity, need, prepared)
	}
	report["fixture"] = prepared

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

	// The colony's answer is journalled: the awakening order, the
	// interactions the quest offers (a structure, the Gleaming monolith, the
	// node) and a dialog answered after the latest interaction was ordered
	// (the node's choice opens when its interaction completes). The native
	// outcome is read after the service releases the game slot.
	var windowsAtLastInteract map[int32]bool
	seenInteracts := 0
	err = na.WaitProgress(ctx, na.Wait{Ceiling: monolithCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		got, err := journalMonolith(ctx, st)
		if err != nil {
			return "", false, err
		}
		if len(got.interacts) != seenInteracts {
			seenInteracts = len(got.interacts)
			windowsAtLastInteract = map[int32]bool{}
			for w := range got.windows {
				windowsAtLastInteract[w] = true
			}
		}
		answeredAfter := false
		for w := range got.windows {
			if !windowsAtLastInteract[w] {
				answeredAfter = true
			}
		}
		done := got.awakened && len(got.interacts) >= minInteractTargets && answeredAfter
		report["awakened"], report["interact_targets"], report["dialog_windows"] = got.awakened, len(got.interacts), len(got.windows)
		return na.Signature(got.awakened, len(got.interacts), len(got.windows)), done, nil
	})
	if err != nil {
		return fmt.Errorf("awakening and disrupt: %w", err)
	}
	report["run_keepalive"] = service.Stop()

	// Native postconditions: the monolith is disrupted (never embraced), the
	// questline has ended and every colonist is alive.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	reply, err := h.Call(ctx, "monolith-inspect", monolithInspectTool, map[string]any{"colonistIds": strings.Join(colonists, ",")})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", monolithInspectTool, reply)
	}
	report["inspect"] = reply
	if embraced, _ := na.AsBool(reply["embraced"]); embraced {
		return fmt.Errorf("the monolith was embraced: %#v", reply)
	}
	if disrupted, _ := na.AsBool(reply["disrupted"]); !disrupted {
		return fmt.Errorf("the monolith is not disrupted (level %v %v, highest %v): %#v", reply["level"], reply["levelDef"], reply["highestLevelReached"], reply)
	}
	if ended, _ := na.AsBool(reply["questlineEnded"]); !ended {
		return fmt.Errorf("the monolith is disrupted but the questline has not ended: %#v", reply)
	}
	for _, raw := range na.AsSlice(reply["colonists"]) {
		row, _ := na.AsMap(raw)
		if found, _ := na.AsBool(row["found"]); !found {
			return fmt.Errorf("colonist %v is gone: %v", row["id"], row)
		}
		if dead, _ := na.AsBool(row["dead"]); dead {
			return fmt.Errorf("colonist %v died in the awakening: %v", row["id"], row)
		}
	}
	return nil
}
