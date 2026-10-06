// The waste/haul case exercises the native waste containment vertical
// (issue #27) through GiveJobIntent HaulWaste on Actions/Apply (#940,
// NativeWasteOperations.cs). A genuinely unwanted item (a disposable
// WoodLog) is hauled by a real native hauling WorkGiver job, issued to an
// undrafted colonist, into a player-designated dirty outdoor stockpile;
// real game ticks move it and independent cell re-reads
// (rimgovernor/observations_get_cells with things requested) confirm it,
// not just the applied result.
//
// Uses the disposable test/waste_fixture fixture (scripts/fixtures/
// WasteFixture.cs): one hauling-capable colonist, one rotten anonymous
// corpse, one unwanted WoodLog, one forbidden (protected) Steel stack, and a
// player-designated dirty outdoor dumping stockpile.
package waste

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "waste/haul",
		Scope: "GiveJobIntent HaulWaste dispatch: a genuinely unwanted item is actually " +
			"hauled by a real native hauling WorkGiver job into a real player-designated dirty stockpile, " +
			"forbidden-item protection refusal, real position change observed via native ticks and independent " +
			"cell re-reads (not just an applied result), and key replay idempotency.",
		Start: cases.Fixture{Op: "test/waste_fixture", Args: map[string]any{"burial": false}, On: cases.LabStart()},
		// Every item and the stockpile are staged by the fixture; the wild map
		// is unobserved (#333).
		QuietWorld: true,
		Budget:     5 * time.Minute,
		Run:        run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Fixture: one hauling colonist, one rotten anonymous corpse, one
	// unwanted WoodLog, one forbidden (protected) Steel stack, and a dirty
	// outdoor dumping stockpile. Run before acquiring authority, mirroring
	// recoveryserviceaccept's own ordering (NativeControlAuthority.
	// RevokeExternal revokes any held lease for such external activity).
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	pawnID := na.AsString(prepared["pawn"])
	corpseID := na.AsString(prepared["corpse"])
	unwantedID := na.AsString(prepared["unwanted"])
	protectedID := na.AsString(prepared["protectedItem"])
	if pawnID == "" || corpseID == "" || unwantedID == "" || protectedID == "" {
		return fmt.Errorf("prepare: missing fixture ids: %#v", prepared)
	}
	destination, _ := na.AsMap(prepared["destination"])
	unwantedCellRaw, _ := na.AsMap(prepared["unwantedCell"])
	protectedCellRaw, _ := na.AsMap(prepared["protectedCell"])
	unwantedCell := map[string]any{"x": unwantedCellRaw["x"], "z": unwantedCellRaw["z"]}
	protectedCell := map[string]any{"x": protectedCellRaw["x"], "z": protectedCellRaw["z"]}
	// The unwanted WoodLog and the forbidden Steel each sit on their own
	// separate cell (a second/third non-stacking item placed directly onto
	// a cell that already holds one was observed live to fail outright),
	// so every "source" scan below covers both cells, not one shared cell.
	sourceCells := []map[string]any{unwantedCell, protectedCell}
	destCellA := map[string]any{"x": destination["x"], "z": destination["z"]}
	destCellB := map[string]any{"x": na.AsNumber(destination["x"]) + 1, "z": destination["z"]}
	report["fixture_pawn"] = pawnID
	report["fixture_unwanted"] = unwantedID
	report["fixture_protected"] = protectedID
	report["fixture_corpse"] = corpseID

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// cellThings reads the things over cells' bounding rect through
	// rimgovernor/observations_get_cells with things requested. Returns
	// every thing row standing on one of the cells.
	cellThings := func(label string, cells []map[string]any) ([]map[string]any, error) {
		want := map[[2]float64]bool{}
		lo, hi := [2]float64{math.Inf(1), math.Inf(1)}, [2]float64{math.Inf(-1), math.Inf(-1)}
		for _, cell := range cells {
			x, z := na.AsNumber(cell["x"]), na.AsNumber(cell["z"])
			want[[2]float64{x, z}] = true
			lo, hi = [2]float64{math.Min(lo[0], x), math.Min(lo[1], z)}, [2]float64{math.Max(hi[0], x), math.Max(hi[1], z)}
		}
		reply, err := h.Wire(ctx, label, "observations_get_cells", map[string]any{
			"scope":     map[string]any{"expectedIdentity": identity},
			"rectangle": map[string]any{"minimum": map[string]any{"x": lo[0], "z": lo[1]}, "maximum": map[string]any{"x": hi[0], "z": hi[1]}},
			"things":    true,
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		var all []map[string]any
		for _, raw := range na.AsSlice(observed["things"]) {
			row, _ := na.AsMap(raw)
			thing, _ := na.AsMap(row["thing"])
			position, _ := na.AsMap(thing["position"])
			if position != nil && want[[2]float64{na.AsNumber(position["x"]), na.AsNumber(position["z"])}] {
				all = append(all, row)
			}
		}
		return all, nil
	}
	findThing := func(things []map[string]any, id string) (map[string]any, bool) {
		for _, row := range things {
			thing, _ := na.AsMap(row["thing"])
			if thing != nil && na.AsString(thing["id"]) == id {
				return row, true
			}
		}
		return nil, false
	}
	apply := func(key, thing string) (map[string]any, error) {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity,
			"actions": []any{map[string]any{"key": key, "giveJob": map[string]any{"pawn": map[string]any{"id": pawnID}, "job": "HaulWaste", "targets": []any{map[string]any{"id": thing}}, "options": map[string]any{"unwanted": true}}}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result: %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}

	beforeSource, err := cellThings("before-source", sourceCells)
	if err != nil {
		return err
	}
	if _, ok := findThing(beforeSource, unwantedID); !ok {
		return fmt.Errorf("before-source: unwanted item not found at the fixture source cell: %#v", beforeSource)
	}
	if _, ok := findThing(beforeSource, protectedID); !ok {
		return fmt.Errorf("before-source: protected item not found at the fixture source cell: %#v", beforeSource)
	}
	report["waste_before_source_things"] = len(beforeSource)

	// A forbidden (protected) item is refused even when declared unwanted:
	// Protection() outranks Kind().
	protected, err := apply("waste-protected", protectedID)
	if err != nil {
		return err
	}
	if protected["refused"] == nil {
		return fmt.Errorf("waste-protected: expected a refusal for a forbidden item, got %#v", protected)
	}

	haul, err := apply("waste-haul", unwantedID)
	if err != nil {
		return err
	}
	applied, ok := na.AsMap(haul["applied"])
	if !ok {
		return fmt.Errorf("waste-haul: expected an applied result, got %#v", haul)
	}
	observed, _ := na.AsMap(applied["observed"])
	job, _ := na.AsMap(observed["job"])
	if na.AsString(job["pawnId"]) != pawnID || na.AsString(job["jobDef"]) != "HaulToCell" {
		return fmt.Errorf("waste-haul: unexpected applied haul evidence: %#v", job)
	}
	if targetA, _ := na.AsMap(job["targetA"]); na.AsString(targetA["thingId"]) != unwantedID {
		return fmt.Errorf("waste-haul: unexpected haul target: %#v", job)
	}
	replay, err := apply("waste-haul", unwantedID)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, haul) {
		return fmt.Errorf("waste-haul: resent key changed its result: %#v then %#v", haul, replay)
	}

	// Run real game time until the item leaves the source cell and arrives
	// in the dirty stockpile, confirmed by independent cell re-reads.
	for advanced := 0; ; advanced += 300 {
		if advanced >= 3*na.TicksPerDay {
			return fmt.Errorf("waste item was not relocated within %d ticks", advanced)
		}
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
		afterDestination, err := cellThings(fmt.Sprintf("destination-%d", advanced), []map[string]any{destCellA, destCellB})
		if err != nil {
			return err
		}
		if _, ok := findThing(afterDestination, unwantedID); ok {
			break
		}
	}
	afterSource, err := cellThings("after-complete-source", sourceCells)
	if err != nil {
		return err
	}
	if _, ok := findThing(afterSource, unwantedID); ok {
		return fmt.Errorf("after-complete-source: expected the unwanted item to have left the source cell")
	}
	report["waste_relocated"] = true

	return nil
}
