package combatlab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const stripApparel = "Apparel_Duster"

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/strip",
		Scope: "The generic strip op (#1117), a native op contract no snapshot can prove: on lab-open cut to one raider staged downed in a duster, " +
			"one Actions/Apply designate STRIP on the raider applies with Strip designation evidence, a second one refuses as already designated, " +
			"and within 2400 ticks a colonist strips the duster off through ordinary Hauling work. Who to strip is #1079.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool, "rimgovernor/operations_apply"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runStrip,
	})
}

// stripKit keeps one raider, downed and wearing a duster.
func stripKit(f *Fixture, _, _ int) {
	var pawns []Pawn
	raiders := 0
	for _, p := range f.Pawns {
		if p.Side == Hostile {
			if raiders++; raiders > 1 {
				continue
			}
			p.Downed, p.Apparel = true, stripApparel
		}
		pawns = append(pawns, p)
	}
	f.Pawns = pawns
}

func runStrip(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	staged, err := Stage(ctx, h, "lab-open", stripKit)
	if err != nil {
		return err
	}
	hostiles := staged.Hostiles()
	if len(hostiles) != 1 {
		return fmt.Errorf("staged %d hostiles, want 1", len(hostiles))
	}
	raider := hostiles[0]
	report["raider"] = raider
	if worn, err := readWorn(ctx, h, raider); err != nil || !worn[stripApparel] {
		return fmt.Errorf("raider does not wear %s before strip (%v): %v", stripApparel, err, worn)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "strip-acquire", s.Identity()); err != nil {
		return err
	}
	apply := func(key string) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{
			"identity": s.Identity(),
			"actions":  []any{map[string]any{"key": key, "designate": map[string]any{"thingId": raider, "designation": "THING_DESIGNATION_STRIP"}}},
		})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("strip: want one result, got %v", reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	first, err := apply("strip-1")
	if err != nil {
		return err
	}
	report["result"] = first
	applied, ok := na.AsMap(first["applied"])
	if !ok {
		return fmt.Errorf("strip not applied: %v", first)
	}
	effect, _ := na.AsMap(applied["observed"])
	designation, _ := na.AsMap(effect["designation"])
	if na.AsString(designation["designationDef"]) != "Strip" || designation["present"] != true || na.AsString(designation["thingId"]) != raider {
		return fmt.Errorf("strip evidence: want a present Strip designation on %s, got %v", raider, applied)
	}
	second, err := apply("strip-2")
	if err != nil {
		return err
	}
	report["repeat"] = second
	if _, ok := na.AsMap(second["applied"]); ok {
		return fmt.Errorf("a second strip on a designated raider applied: %v", second)
	}
	for step := 0; step < 40; step++ {
		_, tick, err := Tick(ctx, h, 60)
		if err != nil {
			return err
		}
		worn, err := readWorn(ctx, h, raider)
		if err != nil {
			return err
		}
		if !worn[stripApparel] {
			report["stripped_tick"] = tick
			return nil
		}
	}
	return fmt.Errorf("raider still wears %s 2400 ticks after the strip designation", stripApparel)
}

// readWorn is one pawn's worn apparel defs from the lab read.
func readWorn(ctx context.Context, h *na.Harness, id string) (map[string]bool, error) {
	reply, err := h.Call(ctx, "strip-worn", StageTool, map[string]any{"action": "read"})
	if err != nil {
		return nil, err
	}
	for _, r := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(r)
		if na.AsString(row["id"]) != id {
			continue
		}
		out := map[string]bool{}
		for _, w := range na.AsSlice(row["worn"]) {
			if item, ok := na.AsMap(w); ok {
				out[na.AsString(item["def"])] = true
			}
		}
		return out, nil
	}
	return nil, fmt.Errorf("pawn %s missing from the lab read", id)
}
