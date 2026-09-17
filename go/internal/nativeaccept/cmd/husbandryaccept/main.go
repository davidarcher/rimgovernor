// Command husbandryaccept exercises the MaintainHerd-* native husbandry
// dispatch vertical (G01.07e, issues #27 and #17) end to end against a live
// game: the four direct-write animal management orders (SetAnimalTraining,
// SlaughterAnimal, TameAnimal, ReleaseAnimal -- NativeHusbandryOperations.cs)
// issued through the same
// typed rimgovernor/operations_execute wire contract Go's
// executor.runHusbandry drives via bridge.HusbandryWriter, against animals
// read live through rimgovernor/observations_read_husbandry
// (bridge.ReadHusbandryTarget's own read). Unlike the job-issuing
// verticals (mood relief, recovery service), all four orders are immediate
// settings writes with no native job (NativeHusbandryOperations.cs's own
// doc comment), so completion is observed as a settings readback, not a
// tick-driven need/effect recovery. Uses a private disposable fixture
// (test/husbandry_setup, HusbandryFixture.cs) since a deterministic animal
// with a known one-step-remaining trainable, a safe-to-slaughter surplus
// candidate, a releasable animal, a factionless tameable animal and a
// protected (pregnant) animal cannot be relied on from
// native random pawn generation and starting colony state, mirroring
// moodreliefaccept's and animalcontainmentaccept's own fixture-first
// pattern.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const sessionOwner = "native-husbandry-acceptance"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-husbandry-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-husbandry-acceptance"
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
	report := na.NewReport("Native MaintainHerd-* husbandry dispatch vertical: actual SetAnimalTraining/"+
		"SlaughterAnimal/TameAnimal/ReleaseAnimal writes issued through the typed operations contract, exact "+
		"settings/census staleness, pregnant-animal-protected and wrong-faction refusals, immediate settings "+
		"readback (not a native job), the wild census the tame planner reads, replay idempotency and durable lookup.", !*rendered)
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
	if !na.Contains(names, "rimgovernor/observations_read_husbandry") {
		return fmt.Errorf("missing rimgovernor/observations_read_husbandry in discovery")
	}

	// Fixture: single-handler colony, roofed enclosure with bed/food, a
	// near-term-pregnant mother, an unrelated father, a full-producing cow
	// and a dog one training step short of Obedience. Run this BEFORE
	// acquiring authority: the fixture spawns/despawns pawns directly
	// outside any authority.Owned() scope, and NativeControlAuthority
	// revokes Auto for such external activity, mirroring
	// animalcontainmentaccept's/populationcustodyaccept's own ordering.
	prepared, err := h.Call(ctx, "prepare", "test/husbandry_setup", map[string]any{})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return fmt.Errorf("prepare: husbandry_setup refused: %#v", prepared)
	}
	motherID := na.AsString(prepared["mother"])
	fatherID := na.AsString(prepared["father"])
	cowID := na.AsString(prepared["cow"])
	dogID := na.AsString(prepared["dog"])
	wildID := na.AsString(prepared["wild"])
	if motherID == "" || fatherID == "" || cowID == "" || dogID == "" || wildID == "" {
		return fmt.Errorf("prepare: missing fixture animal ids: %#v", prepared)
	}
	report["fixture_mother"] = motherID
	report["fixture_father"] = fatherID
	report["fixture_cow"] = cowID
	report["fixture_dog"] = dogID
	report["fixture_wild"] = wildID

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "grant-auto", identity)
	if err != nil {
		return err
	}
	generation := fmt.Sprint(na.GrantGeneration(grant))

	// herdRow reads the whole census through the exact
	// rimgovernor/observations_read_husbandry call bridge.ReadHusbandryTarget
	// issues, so the settings/census tokens and training/safe-to-slaughter
	// facts used below are exactly what policy.SelectHusbandryMethod and
	// executor.runHusbandry would themselves see.
	herdRowScoped := func(label, animalID string, includeWild bool) (map[string]any, error) {
		request := map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 64}}
		if includeWild {
			request["includeWild"] = true
		}
		reply, err := h.Wire(ctx, label, "observations_read_husbandry", request)
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		completeness, _ := na.AsMap(observed["completeness"])
		page, _ := na.AsMap(completeness["page"])
		if complete, _ := na.AsBool(page["complete"]); !complete || na.AsString(page["nextCursor"]) != "" {
			return nil, fmt.Errorf("%s: expected a single complete husbandry page: %#v", label, observed)
		}
		for _, raw := range na.AsSlice(observed["animals"]) {
			row, _ := na.AsMap(raw)
			pawnState, _ := na.AsMap(row["pawn"])
			pawnRef, _ := na.AsMap(pawnState["pawn"])
			if na.AsString(pawnRef["id"]) == animalID {
				return row, nil
			}
		}
		return nil, fmt.Errorf("%s: animal %s not found in husbandry census", label, animalID)
	}
	herdRow := func(label, animalID string) (map[string]any, error) { return herdRowScoped(label, animalID, false) }
	tokens := func(row map[string]any) (settingsToken, censusToken string, err error) {
		settingsSnapshot, _ := na.AsMap(row["settingsSnapshot"])
		censusSnapshot, _ := na.AsMap(row["censusSnapshot"])
		settingsToken = na.AsString(settingsSnapshot["token"])
		censusToken = na.AsString(censusSnapshot["token"])
		if settingsToken == "" || censusToken == "" {
			return "", "", fmt.Errorf("missing settings or census token: %#v", row)
		}
		return settingsToken, censusToken, nil
	}
	trainingEntry := func(row map[string]any, def string) map[string]any {
		animal, _ := na.AsMap(row["animal"])
		for _, raw := range na.AsSlice(animal["training"]) {
			entry, _ := na.AsMap(raw)
			if na.AsString(entry["defName"]) == def {
				return entry
			}
		}
		return nil
	}

	// --- Preconditions: the fixture's dog genuinely has an available,
	// not-yet-wanted Obedience trainable; the cow is genuinely safe to
	// slaughter; the mother is genuinely protected (near-term pregnancy). ---
	dogRow, err := herdRow("dog-before", dogID)
	if err != nil {
		return err
	}
	obedience := trainingEntry(dogRow, "Obedience")
	if obedience == nil {
		return fmt.Errorf("dog-before: missing Obedience trainable entry: %#v", dogRow)
	}
	if avail, _ := na.AsBool(obedience["available"]); !avail {
		return fmt.Errorf("dog-before: expected Obedience to be available, got %#v", obedience)
	}
	if learned, _ := na.AsBool(obedience["learned"]); learned {
		return fmt.Errorf("dog-before: expected Obedience to be unlearned, got %#v", obedience)
	}
	if wanted, _ := na.AsBool(obedience["wanted"]); wanted {
		return fmt.Errorf("dog-before: expected Obedience to be unwanted before dispatch, got %#v", obedience)
	}
	dogSettingsToken, dogCensusToken, err := tokens(dogRow)
	if err != nil {
		return fmt.Errorf("dog-before: %w", err)
	}

	cowRow, err := herdRow("cow-before", cowID)
	if err != nil {
		return err
	}
	cowAnimal, _ := na.AsMap(cowRow["animal"])
	if safe, _ := na.AsBool(cowAnimal["safeToSlaughter"]); !safe {
		return fmt.Errorf("cow-before: expected the fixture cow to be safe to slaughter, got %#v", cowAnimal)
	}
	if slaughter, _ := na.AsBool(cowAnimal["slaughter"]); slaughter {
		return fmt.Errorf("cow-before: expected no pre-existing slaughter designation, got %#v", cowAnimal)
	}
	cowSettingsToken, cowCensusToken, err := tokens(cowRow)
	if err != nil {
		return fmt.Errorf("cow-before: %w", err)
	}

	motherRow, err := herdRow("mother-before", motherID)
	if err != nil {
		return err
	}
	motherAnimal, _ := na.AsMap(motherRow["animal"])
	if safe, _ := na.AsBool(motherAnimal["safeToSlaughter"]); safe {
		return fmt.Errorf("mother-before: expected the near-term-pregnant mother to be protected, got %#v", motherAnimal)
	}
	motherSettingsToken, motherCensusToken, err := tokens(motherRow)
	if err != nil {
		return fmt.Errorf("mother-before: %w", err)
	}

	entity := func(id, token string) map[string]any {
		return map[string]any{"entityId": id, "expectedSnapshotToken": token}
	}
	trainOperation := func(animalID, settingsToken, censusToken, def string) map[string]any {
		return map[string]any{"setAnimalTraining": map[string]any{
			"animal": entity(animalID, settingsToken), "expectedCensusToken": censusToken, "trainableDef": def,
		}}
	}
	designationOperation := func(command, animalID, settingsToken, censusToken string) map[string]any {
		return map[string]any{command: map[string]any{
			"animal": entity(animalID, settingsToken), "expectedCensusToken": censusToken,
		}}
	}
	slaughterOperation := func(animalID, settingsToken, censusToken string) map[string]any {
		return designationOperation("slaughterAnimal", animalID, settingsToken, censusToken)
	}
	buildRequest := func(actionID string, operation map[string]any) map[string]any {
		return map[string]any{
			"precondition": map[string]any{
				"identity": identity, "expectedGeneration": generation,
				"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": actionID, "attemptId": "1"},
			},
			"operation": operation,
		}
	}
	attemptRef := func(request map[string]any) map[string]any {
		precondition, _ := na.AsMap(request["precondition"])
		return map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	}
	failureCode := func(label string, request map[string]any) (string, error) {
		reply, err := h.Wire(ctx, label, "operations_execute", request)
		if err != nil {
			return "", err
		}
		_, failure, err := na.Outcome(reply, "failure")
		if err != nil {
			return "", err
		}
		return na.AsString(failure["code"]), nil
	}

	// Refusal 1: a stale settings token (as if the animal's designations
	// changed since inspection) is refused before any native write.
	staleSettings := buildRequest("husbandry-stale-settings", trainOperation(dogID, "stale-settings-token-00000000000000000000000000000000", dogCensusToken, "Obedience"))
	if code, err := failureCode("stale-settings", staleSettings); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-settings: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 2: a stale herd-wide census token is refused too, proving
	// ExpectedCensusToken is load-bearing (not just the per-animal token).
	staleCensus := buildRequest("husbandry-stale-census", trainOperation(dogID, dogSettingsToken, "stale-census-token-00000000000000000000000000000000", "Obedience"))
	if code, err := failureCode("stale-census", staleCensus); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("stale-census: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}

	// Refusal 3: the near-term-pregnant mother's own SafeToSlaughter=false
	// fact refuses a slaughter designation attempt outright
	// ("pregnant_animal_protected").
	protectedRequest := buildRequest("husbandry-protected", slaughterOperation(motherID, motherSettingsToken, motherCensusToken))
	if code, err := failureCode("protected-mother", protectedRequest); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("protected-mother: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	report["negative_protected_animal"] = motherID

	// Preview: accepted, but never mutates the live training request.
	trainPreviewReply, err := h.Wire(ctx, "preview-train", "operations_preview", map[string]any{
		"identity": identity, "operation": trainOperation(dogID, dogSettingsToken, dogCensusToken, "Obedience"),
	})
	if err != nil {
		return err
	}
	trainEvaluated, ok := na.AsMap(trainPreviewReply["evaluated"])
	if !ok {
		return fmt.Errorf("preview-train: expected an evaluated reply, got %#v", trainPreviewReply)
	}
	if accepted, _ := na.AsBool(trainEvaluated["accepted"]); !accepted {
		return fmt.Errorf("preview-train: expected the training request to be accepted, got %#v", trainEvaluated)
	}
	afterPreviewRow, err := herdRow("dog-after-preview", dogID)
	if err != nil {
		return err
	}
	if entry := trainingEntry(afterPreviewRow, "Obedience"); entry != nil {
		if wanted, _ := na.AsBool(entry["wanted"]); wanted {
			return fmt.Errorf("dog-after-preview: expected an unchanged wanted flag from a dry-run preview: %#v", entry)
		}
	}

	// Execute: the real native SetWantedRecursive write.
	trainRequest := buildRequest("husbandry-train-execute", trainOperation(dogID, dogSettingsToken, dogCensusToken, "Obedience"))
	trainReply, err := h.Wire(ctx, "execute-train", "operations_execute", trainRequest)
	if err != nil {
		return err
	}
	_, trainReceipt, err := na.Outcome(trainReply, "receipt")
	if err != nil {
		return err
	}
	trainApplied, ok := na.AsMap(trainReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-train: expected an applied outcome, got %#v", trainReceipt)
	}
	trainObserved, _ := na.AsMap(trainApplied["observed"])
	trainAnimalEffect, _ := na.AsMap(trainObserved["animal"])
	if na.AsString(trainAnimalEffect["trainableDef"]) != "Obedience" {
		return fmt.Errorf("execute-train: unexpected training effect: %#v", trainAnimalEffect)
	}
	if wanted, _ := na.AsBool(trainAnimalEffect["wanted"]); !wanted {
		return fmt.Errorf("execute-train: expected the training request to be wanted, got %#v", trainAnimalEffect)
	}

	// Immediate settings readback: SetAnimalTraining is a direct write with
	// no native job, so the wanted flag is expected true right away -- no
	// game ticks are needed to observe it, unlike mood relief's/recovery
	// service's job-issuing verticals.
	afterExecuteRow, err := herdRow("dog-after-execute", dogID)
	if err != nil {
		return err
	}
	if entry := trainingEntry(afterExecuteRow, "Obedience"); entry == nil {
		return fmt.Errorf("dog-after-execute: missing Obedience trainable entry: %#v", afterExecuteRow)
	} else if wanted, _ := na.AsBool(entry["wanted"]); !wanted {
		return fmt.Errorf("dog-after-execute: expected Obedience to be wanted after dispatch, got %#v", entry)
	}
	report["training_dispatched"] = true

	// Replay: the exact same attempt returns an identical receipt.
	trainReplayReply, err := h.Wire(ctx, "replay-train", "operations_execute", trainRequest)
	if err != nil {
		return err
	}
	_, trainReplay, err := na.Outcome(trainReplayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(trainReplay, trainReceipt) {
		return fmt.Errorf("replay-train: replay of the same attempt returned a different receipt")
	}

	// Durable lookup: receipts_lookup independently returns the same receipt.
	trainAttempt := attemptRef(trainRequest)
	trainLookupReply, err := h.Wire(ctx, "lookup-train", "receipts_lookup", trainAttempt)
	if err != nil {
		return err
	}
	_, trainLookup, err := na.Outcome(trainLookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(trainLookup, trainReceipt) {
		return fmt.Errorf("lookup-train: expected the same receipt as execute, got %#v", trainLookup)
	}

	// Progress: a single receipts_observe_progress call confirms the
	// completed effect too -- CompleteInspection is not tick-gated for this
	// direct-write family, so no polling loop is needed.
	trainProgressReply, err := h.Wire(ctx, "progress-train", "receipts_observe_progress", trainAttempt)
	if err != nil {
		return err
	}
	_, trainProgress, err := na.Outcome(trainProgressReply, "progress")
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(trainProgress["completed"]); !ok {
		return fmt.Errorf("progress-train: expected an immediately completed progress, got %#v", trainProgress)
	}

	// --- Slaughter: the same immediate-write shape, on the fixture's
	// genuinely safe-to-slaughter cow. ---
	slaughterRequest := buildRequest("husbandry-slaughter-execute", slaughterOperation(cowID, cowSettingsToken, cowCensusToken))
	slaughterReply, err := h.Wire(ctx, "execute-slaughter", "operations_execute", slaughterRequest)
	if err != nil {
		return err
	}
	_, slaughterReceipt, err := na.Outcome(slaughterReply, "receipt")
	if err != nil {
		return err
	}
	slaughterApplied, ok := na.AsMap(slaughterReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-slaughter: expected an applied outcome, got %#v", slaughterReceipt)
	}
	slaughterObserved, _ := na.AsMap(slaughterApplied["observed"])
	slaughterAnimalEffect, _ := na.AsMap(slaughterObserved["animal"])
	if designated, _ := na.AsBool(slaughterAnimalEffect["slaughterDesignated"]); !designated {
		return fmt.Errorf("execute-slaughter: expected a slaughter designation, got %#v", slaughterAnimalEffect)
	}

	afterSlaughterRow, err := herdRow("cow-after-execute", cowID)
	if err != nil {
		return err
	}
	afterSlaughterAnimal, _ := na.AsMap(afterSlaughterRow["animal"])
	if slaughter, _ := na.AsBool(afterSlaughterAnimal["slaughter"]); !slaughter {
		return fmt.Errorf("cow-after-execute: expected the slaughter designation to be observable, got %#v", afterSlaughterAnimal)
	}
	report["slaughter_dispatched"] = true

	slaughterReplayReply, err := h.Wire(ctx, "replay-slaughter", "operations_execute", slaughterRequest)
	if err != nil {
		return err
	}
	_, slaughterReplay, err := na.Outcome(slaughterReplayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(slaughterReplay, slaughterReceipt) {
		return fmt.Errorf("replay-slaughter: replay of the same attempt returned a different receipt")
	}

	slaughterAttempt := attemptRef(slaughterRequest)
	slaughterLookupReply, err := h.Wire(ctx, "lookup-slaughter", "receipts_lookup", slaughterAttempt)
	if err != nil {
		return err
	}
	_, slaughterLookup, err := na.Outcome(slaughterLookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(slaughterLookup, slaughterReceipt) {
		return fmt.Errorf("lookup-slaughter: expected the same receipt as execute, got %#v", slaughterLookup)
	}

	// --- Tame: the factionless muffalo is absent from the plain herd read,
	// present with includeWild, and reads as tameable; the wild census the
	// routine planner consumes (colony facts upkeep.wildAnimals) agrees. ---
	if _, err := herdRow("wild-plain-read", wildID); err == nil {
		return fmt.Errorf("wild-plain-read: a factionless animal must not appear in the player herd read")
	}
	wildRow, err := herdRowScoped("wild-before", wildID, true)
	if err != nil {
		return err
	}
	wildAnimal, _ := na.AsMap(wildRow["animal"])
	if tameable, _ := na.AsBool(wildAnimal["tameable"]); !tameable {
		return fmt.Errorf("wild-before: expected the fixture muffalo to be tameable, got %#v", wildAnimal)
	}
	if tame, _ := na.AsBool(wildAnimal["tame"]); tame {
		return fmt.Errorf("wild-before: expected no pre-existing tame designation, got %#v", wildAnimal)
	}
	wildSettingsToken, wildCensusToken, err := tokens(wildRow)
	if err != nil {
		return fmt.Errorf("wild-before: %w", err)
	}
	factsReply, err := h.Wire(ctx, "colony-facts-wild", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	_, factsSnapshot, err := na.Outcome(factsReply, "observed")
	if err != nil {
		return err
	}
	upkeepSection, _ := na.AsMap(factsSnapshot["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return fmt.Errorf("colony-facts-wild: upkeep section unavailable: %w", err)
	}
	wildCensusFound := false
	for _, raw := range na.AsSlice(upkeep["wildAnimals"]) {
		row, _ := na.AsMap(raw)
		pawnState, _ := na.AsMap(row["pawn"])
		pawnRef, _ := na.AsMap(pawnState["pawn"])
		if na.AsString(pawnRef["id"]) != wildID {
			continue
		}
		state, _ := na.AsMap(pawnState["animalState"])
		if tameable, _ := na.AsBool(state["tameable"]); !tameable {
			return fmt.Errorf("colony-facts-wild: expected tameable in the wild census, got %#v", state)
		}
		wildCensusFound = true
	}
	if !wildCensusFound {
		return fmt.Errorf("colony-facts-wild: fixture muffalo %s missing from upkeep.wildAnimals", wildID)
	}
	for _, raw := range na.AsSlice(upkeep["animals"]) {
		row, _ := na.AsMap(raw)
		pawnState, _ := na.AsMap(row["pawn"])
		pawnRef, _ := na.AsMap(pawnState["pawn"])
		if na.AsString(pawnRef["id"]) == wildID {
			return fmt.Errorf("colony-facts-wild: factionless muffalo must not be in the player animal census")
		}
	}
	report["wild_census_observed"] = true

	// Refusal: taming a player animal is refused (not found as a wild target).
	if code, err := failureCode("tame-player-animal", buildRequest("husbandry-tame-player", designationOperation("tameAnimal", cowID, cowSettingsToken, cowCensusToken))); err != nil {
		return err
	} else if code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("tame-player-animal: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}

	tameRequest := buildRequest("husbandry-tame-execute", designationOperation("tameAnimal", wildID, wildSettingsToken, wildCensusToken))
	tameReply, err := h.Wire(ctx, "execute-tame", "operations_execute", tameRequest)
	if err != nil {
		return err
	}
	_, tameReceipt, err := na.Outcome(tameReply, "receipt")
	if err != nil {
		return err
	}
	tameApplied, ok := na.AsMap(tameReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-tame: expected an applied outcome, got %#v", tameReceipt)
	}
	tameObserved, _ := na.AsMap(tameApplied["observed"])
	tameAnimalEffect, _ := na.AsMap(tameObserved["animal"])
	if designated, _ := na.AsBool(tameAnimalEffect["tameDesignated"]); !designated {
		return fmt.Errorf("execute-tame: expected a tame designation, got %#v", tameAnimalEffect)
	}
	afterTameRow, err := herdRowScoped("wild-after-execute", wildID, true)
	if err != nil {
		return err
	}
	afterTameAnimal, _ := na.AsMap(afterTameRow["animal"])
	if tame, _ := na.AsBool(afterTameAnimal["tame"]); !tame {
		return fmt.Errorf("wild-after-execute: expected the tame designation to be observable, got %#v", afterTameAnimal)
	}
	if tameable, _ := na.AsBool(afterTameAnimal["tameable"]); tameable {
		return fmt.Errorf("wild-after-execute: a designated animal must no longer read as a tame candidate, got %#v", afterTameAnimal)
	}
	report["tame_dispatched"] = true
	tameReplayReply, err := h.Wire(ctx, "replay-tame", "operations_execute", tameRequest)
	if err != nil {
		return err
	}
	if _, tameReplay, err := na.Outcome(tameReplayReply, "receipt"); err != nil {
		return err
	} else if !na.DeepEqual(tameReplay, tameReceipt) {
		return fmt.Errorf("replay-tame: replay of the same attempt returned a different receipt")
	}
	tameProgressReply, err := h.Wire(ctx, "progress-tame", "receipts_observe_progress", attemptRef(tameRequest))
	if err != nil {
		return err
	}
	_, tameProgress, err := na.Outcome(tameProgressReply, "progress")
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(tameProgress["completed"]); !ok {
		return fmt.Errorf("progress-tame: expected an immediately completed progress, got %#v", tameProgress)
	}

	// --- Release: the same immediate-write shape on the fixture's unbonded,
	// unmastered father muffalo; the pregnant mother is refused. ---
	fatherRow, err := herdRow("father-before", fatherID)
	if err != nil {
		return err
	}
	fatherAnimal, _ := na.AsMap(fatherRow["animal"])
	if safe, _ := na.AsBool(fatherAnimal["safeToRelease"]); !safe {
		return fmt.Errorf("father-before: expected the fixture father to be safe to release, got %#v", fatherAnimal)
	}
	fatherSettingsToken, fatherCensusToken, err := tokens(fatherRow)
	if err != nil {
		return fmt.Errorf("father-before: %w", err)
	}
	if code, err := failureCode("release-stale-settings", buildRequest("husbandry-release-stale", designationOperation("releaseAnimal", fatherID, "stale-settings-token-00000000000000000000000000000000", fatherCensusToken))); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("release-stale-settings: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	releaseRequest := buildRequest("husbandry-release-execute", designationOperation("releaseAnimal", fatherID, fatherSettingsToken, fatherCensusToken))
	releaseReply, err := h.Wire(ctx, "execute-release", "operations_execute", releaseRequest)
	if err != nil {
		return err
	}
	_, releaseReceipt, err := na.Outcome(releaseReply, "receipt")
	if err != nil {
		return err
	}
	releaseApplied, ok := na.AsMap(releaseReceipt["applied"])
	if !ok {
		return fmt.Errorf("execute-release: expected an applied outcome, got %#v", releaseReceipt)
	}
	releaseObserved, _ := na.AsMap(releaseApplied["observed"])
	releaseAnimalEffect, _ := na.AsMap(releaseObserved["animal"])
	if designated, _ := na.AsBool(releaseAnimalEffect["releaseDesignated"]); !designated {
		return fmt.Errorf("execute-release: expected a release designation, got %#v", releaseAnimalEffect)
	}
	afterReleaseRow, err := herdRow("father-after-execute", fatherID)
	if err != nil {
		return err
	}
	afterReleaseAnimal, _ := na.AsMap(afterReleaseRow["animal"])
	if release, _ := na.AsBool(afterReleaseAnimal["release"]); !release {
		return fmt.Errorf("father-after-execute: expected the release designation to be observable, got %#v", afterReleaseAnimal)
	}
	if safe, _ := na.AsBool(afterReleaseAnimal["safeToSlaughter"]); safe {
		return fmt.Errorf("father-after-execute: a release-designated animal must no longer be safe to slaughter, got %#v", afterReleaseAnimal)
	}
	report["release_dispatched"] = true
	releaseLookupReply, err := h.Wire(ctx, "lookup-release", "receipts_lookup", attemptRef(releaseRequest))
	if err != nil {
		return err
	}
	if _, releaseLookup, err := na.Outcome(releaseLookupReply, "receipt"); err != nil {
		return err
	} else if !na.DeepEqual(releaseLookup, releaseReceipt) {
		return fmt.Errorf("lookup-release: expected the same receipt as execute, got %#v", releaseLookup)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}
