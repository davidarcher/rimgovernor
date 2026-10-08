package combatlab

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// siegeBuildTicks bounds the wait for a builder on a frame: the supply
// drop pods land, the raiders haul them to the blueprints and start a
// frame, a few thousand ticks on the vanilla camp.
const (
	siegeBuildTicks = 10000
	siegeStepTicks  = 250
	// siegeFarCells is how far, in cells from the nearest colonist, a
	// camped besieger counts as far away (#929): the lab camp is ~32
	// cells out, past the 20-cell hostile watch.
	siegeFarCells = 25
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/siege-stage",
		Scope: "The lab_stage siege option (#1147) and the siege fight's native facts (#1154): lab-siege stages its raiders under a real LordJob_Siege at the camp spot; within " +
			"10,000 ticks the lord is in LordToil_Siege and at least one raider works a FinishFrame job on a camp frame. While camped, the combat frame's " +
			"emergency census lists an engaging besieger at least 25 cells from every colonist (the fight is open while the camp is far away), and a drafted " +
			"colonist's attack order on the builder's frame applies as AttackStatic on that frame.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runSiegeStage,
	})
}

func runSiegeStage(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	typed, err := typedIdentity(identity)
	if err != nil {
		return err
	}
	frames, err := openCombatFrames(ctx, h, typed)
	if err != nil {
		return err
	}
	defer frames.reader.Close()
	staged, err := Stage(ctx, h, "lab-siege")
	if err != nil {
		return err
	}
	hostiles := staged.Hostiles()
	var last map[string]any
	far := false
	for waited := 0; waited < siegeBuildTicks; waited += siegeStepTicks {
		state, err := tickReadN(ctx, h, siegeStepTicks)
		if err != nil {
			return err
		}
		camped := false
		for _, id := range hostiles {
			p := state.pawns[id]
			if p == nil {
				continue
			}
			last = p
			if na.AsString(p["lordJob"]) != "LordJob_Siege" {
				return fmt.Errorf("raider %s left the siege lord: %v", id, p)
			}
			camped = camped || na.AsString(p["lordToil"]) == "LordToil_Siege"
		}
		if camped && !far {
			if far, err = siegeFarOpen(ctx, frames, hostiles, report); err != nil {
				return err
			}
		}
		for _, id := range hostiles {
			p := state.pawns[id]
			if p == nil || na.AsString(p["lordToil"]) != "LordToil_Siege" || na.AsString(p["job"]) != "FinishFrame" {
				continue
			}
			frame := na.AsString(p["jobThing"])
			report["builder"], report["frame"], report["ticks"] = id, frame, waited+siegeStepTicks
			if !far {
				return fmt.Errorf("a raider builds %s but no combat frame listed an engaging besieger %d+ cells out while camped", frame, siegeFarCells)
			}
			return attackFrame(ctx, s, staged.Colonists()[0], frame)
		}
	}
	return fmt.Errorf("no raider in LordToil_Siege on a FinishFrame job within %d ticks (last %v)", siegeBuildTicks, last)
}

// siegeFarOpen reads the next combat frame and reports whether its
// emergency census lists an engaging besieger at least siegeFarCells from
// the nearest colonist: the threat the fight opens on (#929).
func siegeFarOpen(ctx context.Context, frames *combatFrames, hostiles []string, report na.Report) (bool, error) {
	v, err := frames.snapshot(ctx, 0)
	if err != nil {
		return false, err
	}
	combat, err := bridge.DecodeCombat(v)
	if err != nil {
		return false, err
	}
	for _, t := range combat.Emergency.Facts.Threats {
		d, ok := t.Distance.Value()
		if t.Kind == policy.Hostile && t.Engaging() && slices.Contains(hostiles, string(t.ID)) && ok && d >= siegeFarCells {
			report["farThreat"] = map[string]any{"id": t.ID, "distance": d, "tick": v.GetContext().GetTick()}
			return true, nil
		}
	}
	return false, nil
}

// attackFrame drafts colonist and orders it to attack the besiegers'
// frame (#929): the order applies as AttackStatic and, a tick later, the
// colonist's job targets the frame.
func attackFrame(ctx context.Context, s cases.Session, colonist, frame string) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "siege-frame-acquire", identity); err != nil {
		return err
	}
	if err := draftAll(ctx, h, identity, "siege-draft", []string{colonist}); err != nil {
		return err
	}
	results, err := issue(ctx, h, identity, "siege-frame-attack", []any{
		map[string]any{"pawn": map[string]any{"entityId": colonist}, "attack": map[string]any{"entityId": frame}},
	})
	if err != nil {
		return err
	}
	report["frameAttack"] = results
	if applied, _ := na.AsBool(results[0]["applied"]); !applied || na.AsString(results[0]["jobDef"]) != "AttackStatic" {
		return fmt.Errorf("attack on frame %s: want applied AttackStatic, got %v", frame, results[0])
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	if p := after.pawns[colonist]; na.AsString(p["job"]) != "AttackStatic" || na.AsString(p["jobThing"]) != frame {
		return fmt.Errorf("colonist %s after the frame attack: %v", colonist, p)
	}
	return nil
}
