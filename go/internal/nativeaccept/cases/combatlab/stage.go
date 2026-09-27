package combatlab

import (
	"context"
	"encoding/json"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Staged is a fixture as test/lab_stage read it back: each pawn's load id
// in spec order and the name-free map digest.
type Staged struct {
	Fixture Fixture
	Pawns   []map[string]any
	Digest  string
	Rows    []string
	Faction string
}

// Colonists returns the staged colonists' load ids, spec order.
func (s Staged) Colonists() []string { return s.ids(Colonist) }

// Hostiles returns the staged hostiles' load ids, spec order.
func (s Staged) Hostiles() []string { return s.ids(Hostile) }

func (s Staged) ids(side string) []string {
	var out []string
	for i, p := range s.Fixture.Pawns {
		if p.Side == side {
			out = append(out, na.AsString(s.Pawns[i]["id"]))
		}
	}
	return out
}

// Stage wipes the lab to the fixture's colonists, builds it around the
// wipe's centre and stages it, checking every pawn read back where the
// spec put it, armed as specified, hostile under an assault lord. An edit
// adapts the named fixture for one case (an extra door or weapon) before
// staging, around the lab centre; it may change Colonists.
func Stage(ctx context.Context, h *na.Harness, name string, edits ...func(f *Fixture, cx, cz int)) (Staged, error) {
	probe, err := Build(name, 0, 0)
	if err != nil {
		return Staged{}, err
	}
	for _, edit := range edits {
		edit(&probe, 0, 0)
	}
	wipe, err := h.Call(ctx, "combatlab-wipe-"+name, na.LabStartTool, map[string]any{"colonists": probe.Colonists})
	if err != nil {
		return Staged{}, err
	}
	if ok, _ := na.AsBool(wipe["success"]); !ok {
		return Staged{}, fmt.Errorf("%s wipe for %s refused: %#v", na.LabStartTool, name, wipe)
	}
	center, _ := na.AsMap(wipe["center"])
	cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	f, _ := Build(name, cx, cz)
	for _, edit := range edits {
		edit(&f, cx, cz)
	}
	spec, err := json.Marshal(f)
	if err != nil {
		return Staged{}, err
	}
	reply, err := h.Call(ctx, "combatlab-stage-"+name, StageTool, map[string]any{"spec": string(spec)})
	if err != nil {
		return Staged{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return Staged{}, fmt.Errorf("%s %s refused: %#v", StageTool, name, reply)
	}
	rows := na.AsSlice(reply["pawns"])
	if len(rows) != len(f.Pawns) {
		return Staged{}, fmt.Errorf("%s %s: %d pawns read back, want %d", StageTool, name, len(rows), len(f.Pawns))
	}
	out := Staged{Fixture: f, Digest: na.AsString(reply["digest"]), Faction: na.AsString(reply["faction"])}
	for _, r := range na.AsSlice(reply["digestRows"]) {
		out.Rows = append(out.Rows, na.AsString(r))
	}
	for i, want := range f.Pawns {
		row, _ := na.AsMap(rows[i])
		if err := checkPawn(want, row); err != nil {
			return Staged{}, fmt.Errorf("%s pawn %d: %w", name, i, err)
		}
		out.Pawns = append(out.Pawns, row)
	}
	if things := na.AsSlice(reply["things"]); len(things) != len(f.Things) {
		return Staged{}, fmt.Errorf("%s %s: %d things staged, want %d", StageTool, name, len(things), len(f.Things))
	}
	return out, nil
}

// Position is one pawn as test/lab_stage action read reports it.
type Position struct {
	ID           string
	Side         string
	X, Z         int
	Downed, Dead bool
}

// Read returns every pawn on the lab with its cell, and the game tick.
func Read(ctx context.Context, h *na.Harness) (map[string]Position, int, error) {
	return read(ctx, h, map[string]any{"action": "read"})
}

// Tick runs n synchronous game ticks on the paused lab (no clock, no
// safety stops: the fixture's hostiles are the point), then reads.
func Tick(ctx context.Context, h *na.Harness, n int) (map[string]Position, int, error) {
	return read(ctx, h, map[string]any{"action": "tick", "ticks": n})
}

func read(ctx context.Context, h *na.Harness, args map[string]any) (map[string]Position, int, error) {
	reply, err := h.Call(ctx, "combatlab-"+na.AsString(args["action"]), StageTool, args)
	if err != nil {
		return nil, 0, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, 0, fmt.Errorf("%s read refused: %#v", StageTool, reply)
	}
	out := map[string]Position{}
	for _, r := range na.AsSlice(reply["pawns"]) {
		row, _ := na.AsMap(r)
		downed, _ := na.AsBool(row["downed"])
		dead, _ := na.AsBool(row["dead"])
		p := Position{ID: na.AsString(row["id"]), Side: na.AsString(row["side"]), X: int(na.AsNumber(row["x"])), Z: int(na.AsNumber(row["z"])), Downed: downed, Dead: dead}
		out[p.ID] = p
	}
	return out, int(na.AsNumber(reply["tick"])), nil
}

// Gap is the shortest squared distance between a live hostile and a live
// colonist in pawns; -1 when either side has none.
func Gap(pawns map[string]Position) int {
	best := -1
	for _, h := range pawns {
		if h.Side != Hostile || h.Dead {
			continue
		}
		for _, c := range pawns {
			if c.Side != Colonist || c.Dead {
				continue
			}
			if d := (h.X-c.X)*(h.X-c.X) + (h.Z-c.Z)*(h.Z-c.Z); best < 0 || d < best {
				best = d
			}
		}
	}
	return best
}

func checkPawn(want Pawn, got map[string]any) error {
	// A pod hostile (#870) lands where its arrival mode drops the pod.
	if inPod, _ := na.AsBool(got["inPod"]); inPod {
		want.X, want.Z = int(na.AsNumber(got["x"])), int(na.AsNumber(got["z"]))
	}
	if x, z := int(na.AsNumber(got["x"])), int(na.AsNumber(got["z"])); x != want.X || z != want.Z {
		return fmt.Errorf("at %d,%d, want %d,%d: %#v", x, z, want.X, want.Z, got)
	}
	if na.AsString(got["side"]) != want.Side {
		return fmt.Errorf("side %v, want %s", got["side"], want.Side)
	}
	if na.AsString(got["weapon"]) != want.Weapon {
		return fmt.Errorf("weapon %v, want %s", got["weapon"], want.Weapon)
	}
	if hostile, _ := na.AsBool(got["hostile"]); hostile != (want.Side == Hostile) {
		return fmt.Errorf("hostile %v for side %s", hostile, want.Side)
	}
	if want.Side == Hostile {
		if na.AsString(got["lordJob"]) != "LordJob_AssaultColony" {
			return fmt.Errorf("lord job %v, want LordJob_AssaultColony", got["lordJob"])
		}
		if n := int(na.AsNumber(got["apparel"])); n != 0 {
			return fmt.Errorf("hostile wears %d apparel, want none", n)
		}
	}
	if health := na.AsNumber(got["health"]); health < 0.999 {
		return fmt.Errorf("health %.3f, want full", health)
	}
	return nil
}
