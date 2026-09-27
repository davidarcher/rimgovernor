// The animals/containment case exercises the MaintainAnimalContainment
// native dispatch vertical (G01.07e, issue #27) end to end against a live
// game: one rimgovernor/operations_apply batch of BuildingIntents (#856)
// authors a durable Fence/FenceGate pen shell and a PenMarker exactly the way
// buildingruntime.RoutineAnimalContainmentPlanner's buildShell/placeMarker
// compose it, then real game ticks build it and carry a genuinely uncontained,
// pen-requiring herd animal into that pen -- observed via
// rimgovernor/observations_read_colony_facts's native AnimalFeed/AnimalState
// facts (Contained=true, a non-empty PenId), not just a receipt. A
// non-pen-requiring pet fixture confirms RequiresPen=false is left alone.
// Uses a private disposable fixture (test/containment_construct_prepare,
// AnimalContainmentFixture.cs) since a deterministic legal 6x6 pen-shell room
// with reachable WoodLog and a capable Construction/Handling colonist cannot
// be relied on from native random pawn generation and starting colony state,
// mirroring moodreliefaccept's and populationcustodyaccept's own
// fixture-first pattern.
package animals

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const sessionOwner = "native-animal-containment-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "animals/containment",
		Scope: "Native MaintainAnimalContainment dispatch: a real Fence/FenceGate pen shell and " +
			"PenMarker authored as Actions/Apply building intents, then a genuinely uncontained " +
			"pen-requiring herd animal carried into that pen by real native ticks (observed via " +
			"observations_read_colony_facts, not just a receipt), while a non-pen-requiring pet is left alone.",
		// Sited on the audited baseline: an unpinned debug start draws a
		// fresh world each run and may offer no legal pen room (#716).
		Start:  cases.Fixture{Op: "test/containment_construct_prepare", On: cases.LabStart()},
		Keep:   []string{string(na.LiveNeeds)},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Fixture: clears a legal 6x6 room, spawns just enough WoodLog for the
	// full shell, spawns the uncontained herd animal + non-pen pet, and
	// enables one colonist's Construction/Handling. Run this BEFORE granting
	// Auto so the fixture's own direct spawning bumps the native generation
	// before the first WritePrecondition reads it, mirroring
	// populationcustodyaccept's own ordering. Needs stay live: with every
	// colonist's needs pinned the PenMarker blueprint never gets built
	// within its day budget (observed twice on 2026-09-17), while the
	// unfrozen colony finishes it as it always has.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	animalID := na.AsString(prepared["animal"])
	petID := na.AsString(prepared["pet"])
	if animalID == "" || petID == "" {
		return fmt.Errorf("prepare: missing fixture animal/pet ids: %#v", prepared)
	}
	report["fixture_animal"] = animalID
	report["fixture_pet"] = petID
	room, _ := na.AsMap(prepared["room"])
	roomX, roomZ := int(na.AsNumber(room["x"])), int(na.AsNumber(room["z"]))
	roomWidth, roomHeight := int(na.AsNumber(room["width"])), int(na.AsNumber(room["height"]))
	if roomWidth != 6 || roomHeight != 6 {
		return fmt.Errorf("prepare: unexpected pen shell room: %#v", room)
	}
	report["fixture_room"] = room

	// Authority is a single Auto/Manual mode switch (SIMP02): Actions/Apply
	// applies under current native authority, so Auto is granted once.
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant-auto", identity); err != nil {
		return err
	}

	// Author the whole pen in one Actions/Apply batch (#856): 19 Fence + 1
	// FenceGate matching previewPenShell's perimeter/door layout (door
	// anchoring the south wall's center, Fence elsewhere), and the PenMarker
	// at the shell's nearest-northwest interior corner, as placeMarker picks.
	type cellPlan struct {
		defName, stuff string
		x, z           int
	}
	door := cellPlan{"FenceGate", "WoodLog", roomX + roomWidth/2, roomZ}
	pen := []cellPlan{door}
	for x := roomX; x < roomX+roomWidth; x++ {
		for z := roomZ; z < roomZ+roomHeight; z++ {
			if x == door.x && z == door.z {
				continue
			}
			if x == roomX || x == roomX+roomWidth-1 || z == roomZ || z == roomZ+roomHeight-1 {
				pen = append(pen, cellPlan{"Fence", "WoodLog", x, z})
			}
		}
	}
	if len(pen) != 20 {
		return fmt.Errorf("expected a 20-cell pen shell perimeter (19 Fence + 1 FenceGate), got %d", len(pen))
	}
	pen = append(pen, cellPlan{"PenMarker", "", roomX + 1, roomZ + 1})
	actions := make([]any, 0, len(pen))
	for i, cell := range pen {
		placement := map[string]any{"defName": cell.defName, "x": cell.x, "z": cell.z, "rotation": "ROTATION_NORTH"}
		if cell.stuff != "" {
			placement["stuff"] = cell.stuff
		}
		actions = append(actions, map[string]any{"key": fmt.Sprintf("%s-pen-%d", sessionOwner, i), "building": map[string]any{"placement": placement}})
	}
	request := map[string]any{"identity": identity, "actions": actions}
	reply, err := h.Wire(ctx, "apply-pen", "operations_apply", request)
	if err != nil {
		return err
	}
	results := na.AsSlice(reply["results"])
	if len(results) != len(pen) {
		return fmt.Errorf("apply-pen: expected %d results, got %#v", len(pen), reply)
	}
	for i, raw := range results {
		result, _ := na.AsMap(raw)
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		construction, ok := na.AsMap(observed["construction"])
		if !ok {
			return fmt.Errorf("apply-pen %d (%s): expected an applied construction effect, got %#v", i, pen[i].defName, result)
		}
		if na.AsString(construction["stage"]) != "CONSTRUCTION_STAGE_BLUEPRINT" {
			return fmt.Errorf("apply-pen %d (%s): expected an initial Blueprint stage, got %#v", i, pen[i].defName, construction)
		}
	}
	report["pen_cells_placed"] = len(results)

	// Replay: resending the same keys returns the first results unchanged.
	replay, err := h.Wire(ctx, "replay-pen", "operations_apply", request)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, reply) {
		return fmt.Errorf("replay-pen: resent keys returned different results")
	}

	// --- Observe real containment: run ticks forward until the fixture's
	// prepared handler is independently observed to have actually carried
	// the herd animal into the new pen (Contained=true, a real PenId), not
	// merely inferred from the shell/marker construction finishing. The
	// budget covers building the shell (up to 4 days) and marker as well. ---
	animalRow := func(label, id string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity},
		})
		if err != nil {
			return nil, err
		}
		_, observedSnapshot, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		upkeepSection, _ := na.AsMap(observedSnapshot["upkeep"])
		_, upkeep, err := na.Outcome(upkeepSection, "observed")
		if err != nil {
			return nil, fmt.Errorf("%s: upkeep section unavailable: %w", label, err)
		}
		for _, raw := range na.AsSlice(upkeep["animals"]) {
			row, _ := na.AsMap(raw)
			pawnState, _ := na.AsMap(row["pawn"])
			pawnRef, _ := na.AsMap(pawnState["pawn"])
			if na.AsString(pawnRef["id"]) == id {
				return row, nil
			}
		}
		return nil, fmt.Errorf("%s: animal %s not found in upkeep census", label, id)
	}

	var animalContained map[string]any
	if _, err := na.RunUntil(ctx, h, "observe-contain", 8*na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		row, err := animalRow("contain-poll", animalID)
		if err != nil {
			return "", false, err
		}
		pawnState, _ := na.AsMap(row["pawn"])
		animalState, _ := na.AsMap(pawnState["animalState"])
		if contained, _ := na.AsBool(animalState["contained"]); contained && na.AsString(animalState["penId"]) != "" {
			animalContained = row
			return "", true, nil
		}
		return "", false, nil
	}); err != nil {
		return fmt.Errorf("observe-contain: animal did not become contained: %w", err)
	}
	animalPawnState, _ := na.AsMap(animalContained["pawn"])
	animalState, _ := na.AsMap(animalPawnState["animalState"])
	report["animal_contained"] = true
	report["animal_pen_id"] = na.AsString(animalState["penId"])
	if release, _ := na.AsBool(animalState["release"]); release {
		return fmt.Errorf("observe-contain: expected the contained animal to have no release designation")
	}
	if slaughter, _ := na.AsBool(animalState["slaughter"]); slaughter {
		return fmt.Errorf("observe-contain: expected the contained animal to have no slaughter designation")
	}

	// Independently confirm the fixture's non-pen-requiring pet was left
	// alone: no pen was ever assigned to it.
	petRowFinal, err := animalRow("pet-after", petID)
	if err != nil {
		return err
	}
	petPawn, _ := na.AsMap(petRowFinal["pawn"])
	petAnimalState, _ := na.AsMap(petPawn["animalState"])
	if requiresPen, _ := na.AsBool(petRowFinal["requiresPen"]); requiresPen {
		return fmt.Errorf("pet-after: expected the fixture pet to not require a pen: %#v", petRowFinal)
	}
	if penID := na.AsString(petAnimalState["penId"]); penID != "" {
		return fmt.Errorf("pet-after: expected the non-pen pet to have no pen assigned, got %q", penID)
	}
	report["pet_requires_pen"] = false

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
