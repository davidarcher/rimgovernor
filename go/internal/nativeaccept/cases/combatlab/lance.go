package combatlab

import (
	"context"
	"fmt"
	"slices"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const shockLance = "Apparel_PsychicShockLance"

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/shock-lance",
		Scope: "UseItemIntent on Actions/Apply (#1038), a native op contract and vanilla hediff physics no snapshot can prove: on lab-open cut to one club raider, " +
			"colonist 0 wears a psychic shock lance; one Actions/Apply use_item of the worn lance on the raider applies, the colonist takes the verb's " +
			"UseVerbOnThing job on the raider, and within 1200 ticks the raider is down with PsychicShock and alive. The planner's choice of target " +
			"and wearer is the policy test (population_lance_test.go).",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool, "rimgovernor/operations_apply"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runShockLance,
	})
}

// lanceKit keeps one raider and gives colonist 0 a worn shock lance.
func lanceKit(f *Fixture, _, _ int) {
	var pawns []Pawn
	raiders := 0
	for _, p := range f.Pawns {
		if p.Side == Hostile {
			if raiders++; raiders > 1 {
				continue
			}
		}
		if p.Side == Colonist && p.Index == 0 {
			p.Apparel = shockLance
		}
		pawns = append(pawns, p)
	}
	f.Pawns = pawns
}

func runShockLance(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-open", lanceKit)
	if err != nil {
		return err
	}
	colonists, hostiles := staged.Colonists(), staged.Hostiles()
	if len(colonists) != 3 || len(hostiles) != 1 {
		return fmt.Errorf("staged %d colonists and %d hostiles, want 3 and 1", len(colonists), len(hostiles))
	}
	user, raider, lance := colonists[0], hostiles[0], ""
	for _, row := range staged.Pawns {
		if na.AsString(row["id"]) != user {
			continue
		}
		for _, w := range na.AsSlice(row["worn"]) {
			if item, _ := na.AsMap(w); na.AsString(item["def"]) == shockLance {
				lance = na.AsString(item["id"])
			}
		}
	}
	if lance == "" {
		return fmt.Errorf("colonist 0 wears no %s: %v", shockLance, staged.Pawns)
	}
	report["lance"], report["user"], report["raider"] = lance, user, raider
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "lance-acquire", s.Identity()); err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "lance-apply", "operations_apply", map[string]any{
		"identity": s.Identity(),
		"actions":  []any{map[string]any{"key": "lance-1", "useItem": map[string]any{"pawnId": user, "itemId": lance, "targetId": raider}}},
	})
	if err != nil {
		return err
	}
	results := na.AsSlice(reply["results"])
	if len(results) != 1 {
		return fmt.Errorf("use_item: want one result, got %v", reply)
	}
	result, _ := na.AsMap(results[0])
	report["result"] = result
	if _, ok := na.AsMap(result["applied"]); !ok {
		return fmt.Errorf("use_item not applied: %v", result)
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	if p := after.pawns[user]; na.AsString(p["jobThing"]) != raider || na.AsString(p["job"]) != "UseVerbOnThing" {
		return fmt.Errorf("colonist 0 after use_item: want UseVerbOnThing on the raider, got %v", p)
	}
	for step := 0; step < 20; step++ {
		pawns, tick, err := Tick(ctx, h, 60)
		if err != nil {
			return err
		}
		p, ok := pawns[raider]
		if !ok {
			return fmt.Errorf("raider %s left the lab by tick %d", raider, tick)
		}
		if p.Dead {
			return fmt.Errorf("raider died by tick %d; the lance must down it alive", tick)
		}
		if !p.Downed {
			continue
		}
		full, err := readHediffs(ctx, h, raider)
		if err != nil {
			return err
		}
		report["downed_tick"], report["hediffs"] = tick, full
		if !slices.Contains(full, "PsychicShock") {
			return fmt.Errorf("raider down without PsychicShock: %v", full)
		}
		return nil
	}
	return fmt.Errorf("raider still standing 1200 ticks after the lance order")
}

// readHediffs is one pawn's hediff defs from the lab read.
func readHediffs(ctx context.Context, h *na.Harness, id string) ([]string, error) {
	reply, err := h.Call(ctx, "lance-hediffs", StageTool, map[string]any{"action": "read"})
	if err != nil {
		return nil, err
	}
	for _, r := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(r)
		if na.AsString(row["id"]) != id {
			continue
		}
		var out []string
		for _, def := range na.AsSlice(row["hediffs"]) {
			out = append(out, na.AsString(def))
		}
		return out, nil
	}
	return nil, fmt.Errorf("pawn %s missing from the lab read", id)
}
