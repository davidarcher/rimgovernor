// Command animalcontainmentaccept exercises the MaintainAnimalContainment
// native dispatch vertical (G01.07e, issue #27) end to end against a live
// game: the same ordinary typed PlaceBuilding operation
// (rimgovernor/operations_execute, already generically proven live by
// cmd/constructionaccept and cmd/guardedconstructionaccept) is used to build
// a durable Fence/FenceGate pen shell and a PenMarker exactly the way
// buildingruntime.RoutineAnimalContainmentPlanner's buildShell/placeMarker
// compose it, then real game ticks carry a genuinely uncontained,
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
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-animal-containment-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-animal-containment-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 40*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-animal-containment-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native MaintainAnimalContainment dispatch: a real Fence/FenceGate pen shell and "+
		"PenMarker built through the typed PlaceBuilding operations contract, then a genuinely uncontained "+
		"pen-requiring herd animal carried into that pen by real native ticks (observed via "+
		"observations_read_colony_facts, not just a receipt), while a non-pen-requiring pet is left alone.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
	h := na.NewHarness(client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])

	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/operations_execute") {
		return fmt.Errorf("missing rimgovernor/operations_execute in discovery")
	}

	// Fixture: clears a legal 6x6 room, spawns just enough WoodLog for the
	// full shell, spawns the uncontained herd animal + non-pen pet, and
	// enables one colonist's Construction/Handling. Run this BEFORE granting
	// Auto so the fixture's own direct spawning bumps the native generation
	// before the first WritePrecondition reads it, mirroring
	// populationcustodyaccept's own ordering.
	prepared, err := h.Call(ctx, "prepare", "test/containment_construct_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("prepare: containment_construct_prepare refused: %#v", prepared)
	}
	if na.AsString(prepared["colonyId"]) != na.AsString(identity["colonyId"]) ||
		na.AsString(prepared["loadToken"]) != na.AsString(identity["loadToken"]) {
		return fmt.Errorf("prepare: fixture identity does not match the fresh debug game")
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

	// Authority is a single Auto/Manual mode switch (SIMP02): SetMode(Auto)
	// grants outright at the current generation and there is no lease to
	// renew or lose. Fixture spawning above and the Fast-speed observation
	// windows below do not revoke anything; the per-write generation is simply
	// re-read fresh before each dispatch. grantAuto is re-issued before the
	// marker dispatch purely to mirror the production controller's own
	// re-grant after a long observation window.
	grantAuto := func(label string) error {
		_, err := na.GrantAuto(ctx, h.WireFunc(), label, identity)
		return err
	}
	if err := grantAuto("grant-auto"); err != nil {
		return err
	}

	currentGeneration := func(label string) (any, error) {
		reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
		if err != nil {
			return nil, err
		}
		_, status, err := na.Outcome(reply, "status")
		if err != nil {
			return nil, err
		}
		statusContext, _ := na.AsMap(status["context"])
		return statusContext["nativeGeneration"], nil
	}
	buildRequest := func(actionID string, generation any, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": "1"},
			},
			"operation": operation,
		}
	}
	placeOperation := func(defName, stuff string, x, z int) map[string]any {
		placement := map[string]any{"defName": defName, "x": x, "z": z, "rotation": "ROTATION_NORTH"}
		if stuff != "" {
			placement["stuff"] = stuff
		}
		return map[string]any{"placeBuilding": map[string]any{"placement": placement}}
	}
	attemptRef := func(request map[string]any) map[string]any {
		precondition, _ := na.AsMap(request["precondition"])
		return map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	}

	// --- Build the pen shell: 19 Fence + 1 FenceGate, matching
	// previewPenShell's own perimeter/door layout exactly (door anchoring the
	// south wall's center, Fence elsewhere). ---
	type cellPlan struct {
		defName string
		x, z    int
	}
	door := cellPlan{"FenceGate", roomX + roomWidth/2, roomZ}
	perimeter := []cellPlan{door}
	for x := roomX; x < roomX+roomWidth; x++ {
		for z := roomZ; z < roomZ+roomHeight; z++ {
			if x == door.x && z == door.z {
				continue
			}
			if x == roomX || x == roomX+roomWidth-1 || z == roomZ || z == roomZ+roomHeight-1 {
				perimeter = append(perimeter, cellPlan{"Fence", x, z})
			}
		}
	}
	if len(perimeter) != 20 {
		return fmt.Errorf("expected a 20-cell pen shell perimeter (19 Fence + 1 FenceGate), got %d", len(perimeter))
	}

	// Preview the door placement once: accepted, but never mutates the live
	// building census, mirroring every other accept tool's own preview check.
	previewReply, err := h.Wire(ctx, "preview-door", "operations_preview", map[string]any{
		"identity": identity, "operation": placeOperation(door.defName, "WoodLog", door.x, door.z),
	})
	if err != nil {
		return err
	}
	previewEvaluated, ok := na.AsMap(previewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-door: expected an evaluated reply, got %#v", previewReply)
	}
	if accepted, _ := na.AsBool(previewEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-door: expected the FenceGate placement to be accepted, got %#v", previewEvaluated)
	}

	// The single SetMode(Auto) above covers this whole run of dispatches;
	// only the per-cell generation is re-read fresh.
	shellAttempts := make([]map[string]any, 0, len(perimeter))
	for i, cell := range perimeter {
		generation, err := currentGeneration(fmt.Sprintf("generation-shell-%d", i))
		if err != nil {
			return err
		}
		request := buildRequest(fmt.Sprintf("pen-shell-%d", i), generation, placeOperation(cell.defName, "WoodLog", cell.x, cell.z))
		reply, err := h.Wire(ctx, fmt.Sprintf("place-shell-%d", i), "operations_execute", request)
		if err != nil {
			return err
		}
		_, receipt, err := na.Outcome(reply, "receipt")
		if err != nil {
			return err
		}
		applied, ok := na.AsMap(receipt["applied"])
		if !ok {
			return fmt.Errorf("place-shell-%d: expected an applied outcome, got %#v", i, receipt)
		}
		observed, _ := na.AsMap(applied["observed"])
		construction, ok := na.AsMap(observed["construction"])
		if !ok {
			return fmt.Errorf("place-shell-%d: expected a construction effect, got %#v", i, observed)
		}
		if na.AsString(construction["stage"]) != "CONSTRUCTION_STAGE_BLUEPRINT" {
			return fmt.Errorf("place-shell-%d: expected an initial Blueprint stage, got %#v", i, construction)
		}
		shellAttempts = append(shellAttempts, attemptRef(request))
	}
	report["shell_cells_placed"] = len(shellAttempts)

	// Observe: run real game time forward until every shell blueprint is
	// actually built by the fixture's prepared handler/builder, not just
	// issued; absence of the blueprint stage never proves completion by
	// itself.
	if _, err := na.ObserveCompleted(ctx, h, "observe-shell", 4*na.TicksPerDay, shellAttempts...); err != nil {
		return err
	}
	report["shell_built"] = true

	// --- Place the PenMarker at the shell's interior, matching placeMarker's
	// own nearest-northwest-interior-corner choice. ---
	markerX, markerZ := roomX+1, roomZ+1
	if err := grantAuto("grant-auto-marker"); err != nil {
		return err
	}
	markerGeneration, err := currentGeneration("generation-marker")
	if err != nil {
		return err
	}
	markerRequest := buildRequest("pen-marker", markerGeneration, placeOperation("PenMarker", "", markerX, markerZ))
	markerReply, err := h.Wire(ctx, "place-marker", "operations_execute", markerRequest)
	if err != nil {
		return err
	}
	_, markerReceipt, err := na.Outcome(markerReply, "receipt")
	if err != nil {
		return err
	}
	markerApplied, ok := na.AsMap(markerReceipt["applied"])
	if !ok {
		return fmt.Errorf("place-marker: expected an applied outcome, got %#v", markerReceipt)
	}
	markerObserved, _ := na.AsMap(markerApplied["observed"])
	if _, ok := na.AsMap(markerObserved["construction"]); !ok {
		return fmt.Errorf("place-marker: expected a construction effect, got %#v", markerObserved)
	}
	markerAttempt := attemptRef(markerRequest)

	if _, err := na.ObserveCompleted(ctx, h, "observe-marker", na.TicksPerDay, markerAttempt); err != nil {
		return err
	}
	report["marker_built"] = true

	// Replay: the exact same marker attempt returns an identical receipt.
	replayReply, err := h.Wire(ctx, "replay-marker", "operations_execute", markerRequest)
	if err != nil {
		return err
	}
	_, replay, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, markerReceipt) {
		return fmt.Errorf("replay-marker: replay of the same attempt returned a different receipt")
	}
	lookupReply, err := h.Wire(ctx, "lookup-marker", "receipts_lookup", markerAttempt)
	if err != nil {
		return err
	}
	_, lookup, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookup, markerReceipt) {
		return fmt.Errorf("lookup-marker: expected the same receipt as execute, got %#v", lookup)
	}

	// --- Observe real containment: run ticks forward until the fixture's
	// prepared handler is independently observed to have actually carried
	// the herd animal into the new pen (Contained=true, a real PenId), not
	// merely inferred from the shell/marker construction finishing. ---
	animalRow := func(label, id string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 256},
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
	if _, err := na.RunUntil(ctx, h, "observe-contain", 3*na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
