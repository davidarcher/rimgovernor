// Package anomaly holds the Anomaly acceptance cases (epic #1694).
//
// The anomaly/hold-study case (#1747) proves a colony captures a downed
// entity into its containment cell, holds it and studies it.
//
// The anomaly/horror-incident case (#1748) proves a colony survives a horror
// incident: the game's own Anomaly threat incident arrives at the staged
// colony, the served defense planner answers it with the combat tactics of
// #1739, and natively every colonist is alive and no arrival is left on its
// feet.
package anomaly

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combatlab"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	incidentTool = "test/horror_incident"
	inspectTool  = "test/horror_inspect"
	// letterPauseModeTool sets Prefs.AutomaticPauseMode
	// (scripts/fixtures/LetterFixture.cs): a threat letter would pause the
	// served game.
	letterPauseModeTool = "test/letter_pause_mode"
	// fightCeiling bounds the journal wait in wall time on a Fast served
	// clock; the stall budget ends it earlier when nothing moves and
	// fightTicks is the game-time bound.
	fightCeiling = 10 * time.Minute
	fightTicks   = na.TicksPerDay
)

func init() {
	cases.Register(cases.Case{
		Name: "anomaly/horror-incident",
		Scope: "Issue #1748: a colony of four riflemen behind a sandbag line survives an Anomaly threat incident: the game's own incident " +
			"worker fires an incident whose arrivals are a melee entity pack, the served defense planner answers it through the combat " +
			"tactics of #1739, and after the fight every colonist is alive and no arrival is up and on the map (dead, downed or gone). " +
			"A Go snapshot over recorded facts cannot cover it: the incident worker's arrivals, the entities' own AI and attacks, the " +
			"colonists' drafted fire and the deaths are vanilla's, asserted on the live pawns; the tactic choice itself is " +
			"policy/combat_anomaly_test.go. The fixture picks the incident from the game's defs by what it spawns, not by name.",
		Start:       cases.Lab{Colonists: 4},
		Expansions:  []string{"ludeon.rimworld.anomaly"},
		NoKeep:      true,
		RequiredOps: []string{na.LabStartTool, combatlab.StageTool, letterPauseModeTool, incidentTool, inspectTool},
		QuietWorld:  true,
		// A checkpoint capture pauses the served game mid-fight.
		NoCheckpoint: true,
		Serve:        &cases.ServeSpec{Families: []string{"defense", "tend", "rescue"}, PlayerSpeed: "Fast", Prefix: "anomaly-horror-incident"},
		Budget:       15 * time.Minute,
		Run:          runHorrorIncident,
	})
}

// fightState reads the review's ActiveCombat binding: bound, and its need.
func fightState(ctx context.Context, st *store.Store) (review store.Rounds, bound bool, need domain.NeedState, err error) {
	review, err = st.LoadRounds(ctx)
	if err != nil {
		return review, false, "", nil
	}
	binding, ok := review.Incident(policy.ActiveCombat)
	return review, ok, binding.Need, nil
}

func runHorrorIncident(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	if _, err := combatlab.Stage(ctx, h, "lab-horror"); err != nil {
		return err
	}
	if reply, err := h.Call(ctx, "pause-mode-never", letterPauseModeTool, map[string]any{"mode": "Never"}); err != nil {
		return err
	} else if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", letterPauseModeTool, reply)
	}
	incident, err := h.Call(ctx, "horror-incident", incidentTool, nil)
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(incident["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", incidentTool, incident)
	}
	colonists, threats := idList(incident["colonistIds"]), idList(incident["threatIds"])
	if len(colonists) != 4 || len(threats) == 0 {
		return fmt.Errorf("fixture: unexpected horror staging: %#v", incident)
	}
	report["incident"], report["fixture"] = incident["incident"], incident

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

	// The colony's answer is journalled: the ActiveCombat goal binds when the
	// arrivals are seen and recovers once no live hostile is left. The
	// native outcome is read after the service releases the game slot.
	sawFight := false
	err = na.WaitProgress(ctx, na.Wait{
		Ceiling: fightCeiling, Stall: na.StallBudget(), Terminal: service.Exited, Ticks: fightTicks,
		Tick: func(ctx context.Context) (uint64, error) {
			review, err := st.LoadRounds(ctx)
			return uint64(review.Tick), err
		},
	}, func(ctx context.Context) (string, bool, error) {
		review, bound, need, err := fightState(ctx, st)
		if err != nil {
			return "", false, err
		}
		sawFight = sawFight || bound
		resolved := sawFight && (!bound || need == domain.NeedRecovered)
		report["combat_bound"], report["combat_need"], report["saw_fight"] = bound, string(need), sawFight
		return na.Signature(bound, need, uint64(review.Tick)/(na.TicksPerDay/24)), resolved, nil
	})
	if err != nil {
		return fmt.Errorf("combat answer: %w", err)
	}
	report["run_keepalive"] = service.Stop()

	// Native postconditions: every colonist alive, every arrival dead,
	// downed or gone from the map.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	reply, err := h.Call(ctx, "horror-inspect", inspectTool, map[string]any{
		"colonistIds": strings.Join(colonists, ","), "threatIds": strings.Join(threats, ","),
	})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", inspectTool, reply)
	}
	report["inspect"] = reply
	for _, raw := range na.AsSlice(reply["colonists"]) {
		row, _ := na.AsMap(raw)
		if found, _ := na.AsBool(row["found"]); !found {
			return fmt.Errorf("colonist %v is gone: %v", row["id"], row)
		}
		if dead, _ := na.AsBool(row["dead"]); dead {
			return fmt.Errorf("colonist %v died in the horror incident: %v", row["id"], row)
		}
	}
	for _, raw := range na.AsSlice(reply["threats"]) {
		row, _ := na.AsMap(raw)
		if found, _ := na.AsBool(row["found"]); !found {
			continue
		}
		dead, _ := na.AsBool(row["dead"])
		destroyed, _ := na.AsBool(row["destroyed"])
		spawned, _ := na.AsBool(row["spawned"])
		downed, _ := na.AsBool(row["downed"])
		if !dead && !destroyed && spawned && !downed {
			return fmt.Errorf("arrival %v is still up on the map after the fight: %v", row["id"], row)
		}
	}
	return nil
}

func idList(raw any) []string {
	var out []string
	for _, v := range na.AsSlice(raw) {
		if id := na.AsString(v); id != "" {
			out = append(out, id)
		}
	}
	return out
}
