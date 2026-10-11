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
	// Things are the staged things' load ids, spec order.
	Things  []string
	Digest  string
	Rows    []string
	Faction string
	// Seed is the seed test/lab_stage echoed back.
	Seed int
	// Center is the lab wipe's centre the fixture was built around.
	Center Cell
}

// WithSeed is a Stage edit that stages the fixture under seed (see
// Fixture.Seed).
func WithSeed(seed int) func(f *Fixture, cx, cz int) {
	return func(f *Fixture, _, _ int) { f.Seed = seed }
}

// Colonists returns the staged colonists' load ids, spec order.
func (s Staged) Colonists() []string { return s.ids(Colonist) }

// Hostiles returns the staged hostiles' load ids, spec order.
func (s Staged) Hostiles() []string { return s.ids(Hostile) }

// Animals returns the staged player animals' load ids, spec order.
func (s Staged) Animals() []string { return s.ids(Animal) }

// Manhunters returns the staged manhunters' load ids, spec order.
func (s Staged) Manhunters() []string { return s.ids(Manhunter) }

// Mechs returns the staged mechanoids' load ids, spec order.
func (s Staged) Mechs() []string { return s.ids(Mech) }

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
	out := Staged{Fixture: f, Digest: na.AsString(reply["digest"]), Faction: na.AsString(reply["faction"]), Seed: int(na.AsNumber(reply["seed"])), Center: Cell{cx, cz}}
	if out.Seed != f.Seed {
		return Staged{}, fmt.Errorf("%s %s: staged under seed %d, want %d", StageTool, name, out.Seed, f.Seed)
	}
	for _, r := range na.AsSlice(reply["digestRows"]) {
		out.Rows = append(out.Rows, na.AsString(r))
	}
	for i, want := range f.Pawns {
		row, _ := na.AsMap(rows[i])
		lord := "LordJob_AssaultColony"
		if f.Siege != nil {
			lord = "LordJob_Siege"
		}
		if err := checkPawn(want, row, lord); err != nil {
			return Staged{}, fmt.Errorf("%s pawn %d: %w", name, i, err)
		}
		out.Pawns = append(out.Pawns, row)
	}
	things := na.AsSlice(reply["things"])
	if len(things) != len(f.Things) {
		return Staged{}, fmt.Errorf("%s %s: %d things staged, want %d", StageTool, name, len(things), len(f.Things))
	}
	for _, t := range things {
		row, _ := na.AsMap(t)
		out.Things = append(out.Things, na.AsString(row["id"]))
	}
	return out, nil
}

// Reseed sets the game's RNG from seed through test/lab_stage action reseed
// (#2849) and returns the next draws the native read without consuming
// them. It fixes the RNG start only: once two runs' tactics diverge, their
// draws diverge too, so a paired comparison across tactics is a comparison
// of distributions, not of identical dice.
func Reseed(ctx context.Context, h *na.Harness, seed int) ([]int, error) {
	if seed == 0 {
		return nil, fmt.Errorf("reseed needs a non-zero seed")
	}
	spec, _ := json.Marshal(map[string]int{"seed": seed})
	reply, err := h.Call(ctx, fmt.Sprintf("combatlab-reseed-%d", seed), StageTool, map[string]any{"action": "reseed", "spec": string(spec)})
	if err != nil {
		return nil, err
	}
	return parseReseed(reply, seed)
}

// parseReseed checks a reseed reply echoed the seed and carried draws.
func parseReseed(reply map[string]any, seed int) ([]int, error) {
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s reseed %d refused: %#v", StageTool, seed, reply)
	}
	if got := int(na.AsNumber(reply["seed"])); got != seed {
		return nil, fmt.Errorf("%s reseeded under %d, want %d", StageTool, got, seed)
	}
	var draws []int
	for _, d := range na.AsSlice(reply["draws"]) {
		draws = append(draws, int(na.AsNumber(d)))
	}
	if len(draws) == 0 {
		return nil, fmt.Errorf("%s reseed %d read back no draws: %#v", StageTool, seed, reply)
	}
	return draws, nil
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

func checkPawn(want Pawn, got map[string]any, lord string) error {
	// A pod hostile lands where its arrival mode drops the pod.
	if inPod, _ := na.AsBool(got["inPod"]); inPod {
		want.X, want.Z = int(na.AsNumber(got["x"])), int(na.AsNumber(got["z"]))
	}
	j := want.Jitter
	if x, z := int(na.AsNumber(got["x"])), int(na.AsNumber(got["z"])); x < want.X-j || x > want.X+j || z < want.Z-j || z > want.Z+j {
		return fmt.Errorf("at %d,%d, want %d,%d: %#v", x, z, want.X, want.Z, got)
	}
	if na.AsString(got["side"]) != want.Side {
		return fmt.Errorf("side %v, want %s", got["side"], want.Side)
	}
	if na.AsString(got["weapon"]) != want.Weapon {
		return fmt.Errorf("weapon %v, want %s", got["weapon"], want.Weapon)
	}
	if hostile, _ := na.AsBool(got["hostile"]); hostile != (want.Side == Hostile || want.Side == Manhunter || want.Side == Mech) {
		return fmt.Errorf("hostile %v for side %s", hostile, want.Side)
	}
	if want.Side == Hostile {
		if na.AsString(got["lordJob"]) != lord {
			return fmt.Errorf("lord job %v, want %s", got["lordJob"], lord)
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
