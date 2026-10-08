package burial

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const (
	tombStage  = "test/tomb_stage"
	tombSecond = "test/tomb_second"
	tombRead   = "test/tomb_read"
	// tombWindow bounds one vanilla haul into the sarcophagus and the
	// deconstruction work, as burialWindow does for the grave.
	tombWindow = 2 * na.TicksPerDay
	// tombNear is how far (cells) the ejected corpse may land from the
	// sarcophagus: ThingPlaceMode.Near on a 1x2 building.
	tombNear = 4
)

func init() {
	cases.Register(cases.Case{
		Name: "burial/stranger_sarcophagus",
		Scope: "Issue #2338 stranger sarcophagus loop (epic #2209): a colonist hauls a stranger corpse into a fresh sarcophagus and every " +
			"colonist gains exactly one KnowBuriedInSarcophagus; the corpse carries everBuriedInSarcophagus; a second stranger corpse buried " +
			"in the same, now used, sarcophagus gives nothing; a DECONSTRUCT designation (the planner's TombDispose action) ejects the corpse " +
			"beside the cell, spawned, ready for the incinerator path (waste/incineration owns the burn). Vanilla Building_Sarcophagus.Notify_HauledTo, everNonEmpty and " +
			"Building_Casket.Destroy physics that no Go snapshot over recorded facts covers; the planner decision (TombDispose, cap, funding, " +
			"no re-staging of a flagged corpse) is snapshot-tested in policy and buildingruntime.",
		Start:       cases.Fixture{Op: tombStage, On: cases.LabStart()},
		RequiredOps: []string{tombStage, tombSecond, tombRead},
		QuietWorld:  true,
		Keep:        []string{"Mood"},
		Reason:      "Mood stays live: the assertion reads the memory it gains.",
		Budget:      8 * time.Minute,
		Crew:        cases.Crew{Size: 3}, Run: runStrangerSarcophagus,
	})
}

type tombState struct {
	present, holds bool
	memories       []int
	moods          []bool
	corpses        map[string]map[string]any
}

func runStrangerSarcophagus(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	prepared := s.Prepared()
	sarcophagus, first := na.AsString(prepared["sarcophagus"]), na.AsString(prepared["corpse"])
	if sarcophagus == "" || first == "" {
		return fmt.Errorf("prepare: missing fixture ids: %#v", prepared)
	}
	cell := map[string]int{"x": int(na.AsNumber(prepared["sarcophagusX"])), "z": int(na.AsNumber(prepared["sarcophagusZ"]))}
	read := func(label string, ids ...string) (tombState, error) {
		joined := ""
		for _, id := range ids {
			joined += "," + id
		}
		reply, err := h.Call(ctx, label, tombRead, map[string]any{"sarcophagus": sarcophagus, "ids": joined})
		if err != nil {
			return tombState{}, err
		}
		st := tombState{corpses: map[string]map[string]any{}}
		st.present, _ = na.AsBool(reply["present"])
		st.holds, _ = na.AsBool(reply["holds"])
		for _, raw := range na.AsSlice(reply["colonists"]) {
			row, _ := na.AsMap(raw)
			mood, _ := na.AsBool(row["mood"])
			st.moods = append(st.moods, mood)
			st.memories = append(st.memories, int(na.AsNumber(row["memories"])))
		}
		for _, raw := range na.AsSlice(reply["corpses"]) {
			row, _ := na.AsMap(raw)
			st.corpses[na.AsString(row["id"])] = row
		}
		return st, nil
	}
	// until polls label's read every 300 ticks until done holds, bounded.
	until := func(label, what string, ids []string, done func(tombState) bool) (tombState, error) {
		for advanced := 0; ; advanced += 300 {
			st, err := read(fmt.Sprintf("%s-%d", label, advanced), ids...)
			if err != nil {
				return st, err
			}
			if done(st) {
				report[label+"_ticks"] = advanced
				return st, nil
			}
			if advanced >= tombWindow {
				return st, fmt.Errorf("%s: not within %d ticks: %#v", what, advanced, st)
			}
			if _, err := s.Advance(ctx, 300); err != nil {
				return st, err
			}
		}
	}
	flag := func(st tombState, id string) bool {
		b, _ := na.AsBool(st.corpses[id]["everBuried"])
		return b
	}
	spawned := func(st tombState, id string) bool {
		b, _ := na.AsBool(st.corpses[id]["spawned"])
		return b
	}
	inside := func(st tombState, id string) bool {
		b, _ := na.AsBool(st.corpses[id]["inSarcophagus"])
		return b
	}
	// everyone asserts every colonist carries want KnowBuriedInSarcophagus memories.
	everyone := func(st tombState, want int, when string) error {
		if len(st.memories) == 0 {
			return fmt.Errorf("%s: no colonist read", when)
		}
		for i, n := range st.memories {
			if !st.moods[i] || n != want {
				return fmt.Errorf("%s: colonist %d has %d %s memories (mood need %t), want %d: %#v", when, i, n, policy.KnowBuriedInSarcophagusThought, st.moods[i], want, st)
			}
		}
		return nil
	}

	// Burial one: the first body ever in a fresh sarcophagus.
	buried, err := until("first_burial", "the stranger corpse was not hauled into the sarcophagus", []string{first},
		func(st tombState) bool { return inside(st, first) })
	if err != nil {
		return err
	}
	if !flag(buried, first) {
		return fmt.Errorf("the buried corpse lacks everBuriedInSarcophagus: %#v", buried)
	}
	if err := everyone(buried, 1, "after the first burial"); err != nil {
		return err
	}
	report["colonists"] = len(buried.memories)

	// Burial two: the same sarcophagus again, with a fresh stranger corpse.
	reply, err := h.Call(ctx, "second-stage", tombSecond, map[string]any{"sarcophagus": sarcophagus})
	if err != nil {
		return err
	}
	second := na.AsString(reply["second"])
	if second == "" {
		return fmt.Errorf("tomb_second: no second corpse: %#v", reply)
	}
	again, err := until("second_burial", "the second stranger corpse was not hauled into the sarcophagus", []string{first, second},
		func(st tombState) bool { return inside(st, second) })
	if err != nil {
		return err
	}
	if err := everyone(again, 1, "after the second burial"); err != nil {
		return err
	}
	if !flag(again, first) {
		return fmt.Errorf("the ejected first corpse lost everBuriedInSarcophagus: %#v", again)
	}
	report["second_burial_flag"] = flag(again, second)

	// Disposal: the planner's TombDispose action, a DECONSTRUCT designation.
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "tomb-authority", s.Identity()); err != nil {
		return err
	}
	designation := map[string]any{"designation": "THING_DESIGNATION_DECONSTRUCT", "target": map[string]any{"id": sarcophagus}, "guard": "DESIGNATION_GUARD_ENCLOSURE"}
	applied, err := h.Wire(ctx, "tomb-dispose", "operations_apply", map[string]any{"identity": s.Identity(),
		"actions": []any{map[string]any{"key": "tomb-dispose", "designate": designation}}})
	if err != nil {
		return err
	}
	results := na.AsSlice(applied["results"])
	if len(results) != 1 {
		return fmt.Errorf("tomb-dispose: expected one result: %#v", applied)
	}
	result, _ := na.AsMap(results[0])
	if _, ok := na.AsMap(result["applied"]); !ok {
		return fmt.Errorf("tomb-dispose: not applied: %#v", result)
	}
	gone, err := until("deconstruct", "the sarcophagus was not deconstructed", []string{first, second},
		func(st tombState) bool { return !st.present })
	if err != nil {
		return err
	}
	if !spawned(gone, second) || inside(gone, second) {
		return fmt.Errorf("the corpse was not ejected into the open: %#v", gone.corpses[second])
	}
	x, z := int(na.AsNumber(gone.corpses[second]["x"])), int(na.AsNumber(gone.corpses[second]["z"]))
	dx, dz := x-cell["x"], z-cell["z"]
	report["ejected_at"] = map[string]any{"x": x, "z": z, "sarcophagus": cell}
	report["ejected_rot"] = gone.corpses[second]["rot"]
	if dx < -tombNear || dx > tombNear || dz < -tombNear || dz > tombNear {
		return fmt.Errorf("the corpse landed (%d,%d), more than %d cells from the sarcophagus at (%d,%d)", x, z, tombNear, cell["x"], cell["z"])
	}
	report["ejected_flag"] = flag(gone, second)
	// Deconstruction gives no memory either.
	return everyone(gone, 1, "after the deconstruction")
}
