// The husbandry/dispatch case exercises the MaintainHerd-* native husbandry
// vertical (G01.07e, issues #27, #17 and #941) end to end against a live
// game: each direct-write animal order (train, slaughter, tame, release,
// allowed area, master, following and the designation cancels) is one
// HusbandryIntent on rimgovernor/operations_apply, the same Actions/Apply
// call Go's plain intent path sends, against animals read live through
// rimgovernor/observations_read_husbandry. All of them are immediate
// settings writes with no native job, so applied is the effect and the case
// reads the settings back at once. Uses a private disposable fixture
// (test/husbandry_setup, HusbandryFixture.cs) since a deterministic animal
// with a known one-step-remaining trainable, a safe-to-slaughter surplus
// candidate, a releasable animal, a factionless tameable animal and a
// protected (pregnant) animal cannot be relied on from native random pawn
// generation and starting colony state.
package husbandry

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "husbandry/dispatch",
		Scope: "Native MaintainHerd-* husbandry vertical: train/slaughter/tame/release HusbandryIntents applied through " +
			"operations_apply, pregnant-animal-protected and wrong-faction refusals, immediate settings readback (not a " +
			"native job), the wild census the tame planner reads, allowed-area/master/following writes with their " +
			"obedience and area-existence refusals, designation cancels, and resends of orders that already hold applying again.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/husbandry_setup"},
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	// Fixture: single-handler colony, roofed enclosure with bed/food, a
	// near-term-pregnant mother, an unrelated father, a full-producing cow
	// and a dog one training step short of Obedience. Run this BEFORE
	// acquiring authority: the fixture spawns/despawns pawns directly
	// outside any authority.Owned() scope, and NativeControlAuthority
	// revokes Auto for such external activity, mirroring
	// animalcontainmentaccept's/populationcustodyaccept's own ordering.
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	if !na.Contains(s.Names(), "rimgovernor/observations_read_husbandry") {
		return fmt.Errorf("missing rimgovernor/observations_read_husbandry in discovery")
	}
	motherID := na.AsString(prepared["mother"])
	fatherID := na.AsString(prepared["father"])
	cowID := na.AsString(prepared["cow"])
	dogID := na.AsString(prepared["dog"])
	wildID := na.AsString(prepared["wild"])
	guardID := na.AsString(prepared["guard"])
	handlerID := na.AsString(prepared["handler"])
	areaID := na.AsString(prepared["area"])
	if motherID == "" || fatherID == "" || cowID == "" || dogID == "" || wildID == "" || guardID == "" || handlerID == "" || areaID == "" {
		return fmt.Errorf("prepare: missing fixture ids: %#v", prepared)
	}
	report["fixture_guard"] = guardID
	report["fixture_area"] = areaID
	report["fixture_mother"] = motherID
	report["fixture_father"] = fatherID
	report["fixture_cow"] = cowID
	report["fixture_dog"] = dogID
	report["fixture_wild"] = wildID

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant-auto", identity); err != nil {
		return err
	}

	// herdRow reads the whole census through rimgovernor/observations_read_husbandry,
	// the read the husbandry planners consume.
	herdRowScoped := func(label, animalID string, includeWild bool) (map[string]any, error) {
		request := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}
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
		for _, raw := range na.AsSlice(observed["animals"]) {
			row, _ := na.AsMap(raw)
			if na.PawnRef(row) == animalID {
				return row, nil
			}
		}
		return nil, fmt.Errorf("%s: animal %s not found in husbandry census", label, animalID)
	}
	herdRow := func(label, animalID string) (map[string]any, error) { return herdRowScoped(label, animalID, false) }
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
	// not-yet-wanted Obedience trainable; the cow is genuinely slaughterable;
	// the mother reads pregnant (policy protects her, native does not refuse). ---
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

	cowRow, err := herdRow("cow-before", cowID)
	if err != nil {
		return err
	}
	cowAnimal, _ := na.AsMap(cowRow["animal"])
	if designatable, _ := na.AsBool(cowAnimal["slaughterDesignatable"]); !designatable {
		return fmt.Errorf("cow-before: expected the slaughter designator to accept the fixture cow, got %#v", cowAnimal)
	}
	for _, flag := range []string{"downed", "inMentalState", "pregnant", "colonistBonded"} {
		if set, _ := na.AsBool(cowAnimal[flag]); set {
			return fmt.Errorf("cow-before: expected %s clear on the fixture cow, got %#v", flag, cowAnimal)
		}
	}
	if slaughter, _ := na.AsBool(cowAnimal["slaughter"]); slaughter {
		return fmt.Errorf("cow-before: expected no pre-existing slaughter designation, got %#v", cowAnimal)
	}

	motherRow, err := herdRow("mother-before", motherID)
	if err != nil {
		return err
	}
	motherAnimal, _ := na.AsMap(motherRow["animal"])
	if pregnant, _ := na.AsBool(motherAnimal["pregnant"]); !pregnant {
		return fmt.Errorf("mother-before: expected the raw pregnant fact on the near-term mother, got %#v", motherAnimal)
	}

	// apply sends one HusbandryIntent on Actions/Apply (#941) and returns
	// the action's result: applied carries the animal evidence, refused
	// the native reason.
	apply := func(label, key, animalID, order string, extra map[string]any) (map[string]any, error) {
		intent := map[string]any{"animalId": animalID, "order": "HUSBANDRY_ORDER_" + order}
		for k, v := range extra {
			intent[k] = v
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, "husbandry": intent,
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	// applied returns the AnimalEffect of an applied result.
	applied := func(label, key, animalID, order string, extra map[string]any) (map[string]any, error) {
		result, err := apply(label, key, animalID, order, extra)
		if err != nil {
			return nil, err
		}
		receipt, _ := na.AsMap(result["applied"])
		outcome, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(outcome["observed"])
		effect, ok := na.AsMap(observed["animal"])
		if !ok {
			return nil, fmt.Errorf("%s: expected applied animal evidence, got %#v", label, result)
		}
		return effect, nil
	}
	// refused asserts a refusal with the given code whose reason names the
	// rule that failed.
	refused := func(label, key, animalID, order string, extra map[string]any, code, reason string) error {
		result, err := apply(label, key, animalID, order, extra)
		if err != nil {
			return err
		}
		refusal, ok := na.AsMap(result["refused"])
		got := na.AsString(refusal["reason"])
		if !ok || na.AsString(refusal["code"]) != code || !strings.Contains(got, reason) {
			return fmt.Errorf("%s: expected %s %q, got %#v", label, code, reason, result)
		}
		report[strings.ReplaceAll(label, "-", "_")] = got
		return nil
	}

	// Train: the real native SetWantedRecursive write, read back at once:
	// a direct write with no native job, so no game ticks are needed.
	trainEffect, err := applied("apply-train", "husbandry-train", dogID, "TRAIN", map[string]any{"trainableDef": "Obedience"})
	if err != nil {
		return err
	}
	if wanted, _ := na.AsBool(trainEffect["wanted"]); !wanted || na.AsString(trainEffect["trainableDef"]) != "Obedience" {
		return fmt.Errorf("apply-train: expected Obedience wanted, got %#v", trainEffect)
	}
	afterTrainRow, err := herdRow("dog-after-train", dogID)
	if err != nil {
		return err
	}
	if entry := trainingEntry(afterTrainRow, "Obedience"); entry == nil {
		return fmt.Errorf("dog-after-train: missing Obedience trainable entry: %#v", afterTrainRow)
	} else if wanted, _ := na.AsBool(entry["wanted"]); !wanted {
		return fmt.Errorf("dog-after-train: expected Obedience to be wanted, got %#v", entry)
	}
	// A training request that already holds applies again.
	if _, err := applied("reapply-train", "husbandry-train-again", dogID, "TRAIN", map[string]any{"trainableDef": "Obedience"}); err != nil {
		return err
	}
	report["training_dispatched"] = true

	// --- Slaughter: the same immediate-write shape, on the fixture's
	// genuinely slaughterable cow; resending applies again. ---
	for _, label := range []string{"apply-slaughter", "reapply-slaughter"} {
		effect, err := applied(label, "husbandry-"+label, cowID, "SLAUGHTER", nil)
		if err != nil {
			return err
		}
		if designated, _ := na.AsBool(effect["slaughterDesignated"]); !designated {
			return fmt.Errorf("%s: expected a slaughter designation, got %#v", label, effect)
		}
	}
	afterSlaughterRow, err := herdRow("cow-after-slaughter", cowID)
	if err != nil {
		return err
	}
	afterSlaughterAnimal, _ := na.AsMap(afterSlaughterRow["animal"])
	if slaughter, _ := na.AsBool(afterSlaughterAnimal["slaughter"]); !slaughter {
		return fmt.Errorf("cow-after-slaughter: expected the slaughter designation to be observable, got %#v", afterSlaughterAnimal)
	}
	report["slaughter_dispatched"] = true

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
	factsReply, err := h.Wire(ctx, "colony-facts-wild", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity},
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
		if na.PawnRef(row) != wildID {
			continue
		}
		// The tame facts ride the pawn table row (#1343).
		if err := h.JoinPawn(ctx, "colony-facts-wild", identity, row); err != nil {
			return err
		}
		pawnState, _ := na.AsMap(row["pawn"])
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
		if na.PawnRef(row) == wildID {
			return fmt.Errorf("colony-facts-wild: factionless muffalo must not be in the player animal census")
		}
	}
	report["wild_census_observed"] = true

	// Refusal: taming a player animal is refused (not found as a wild target).
	if err := refused("tame-player-animal", "husbandry-tame-player", cowID, "TAME", nil,
		"FAILURE_CODE_NOT_FOUND", "Husbandry refused: animal "+cowID+" is not a wild animal on the map"); err != nil {
		return err
	}
	for _, label := range []string{"apply-tame", "reapply-tame"} {
		effect, err := applied(label, "husbandry-"+label, wildID, "TAME", nil)
		if err != nil {
			return err
		}
		if designated, _ := na.AsBool(effect["tameDesignated"]); !designated {
			return fmt.Errorf("%s: expected a tame designation, got %#v", label, effect)
		}
	}
	afterTameRow, err := herdRowScoped("wild-after-tame", wildID, true)
	if err != nil {
		return err
	}
	afterTameAnimal, _ := na.AsMap(afterTameRow["animal"])
	if tame, _ := na.AsBool(afterTameAnimal["tame"]); !tame {
		return fmt.Errorf("wild-after-tame: expected the tame designation to be observable, got %#v", afterTameAnimal)
	}
	if tameable, _ := na.AsBool(afterTameAnimal["tameable"]); tameable {
		return fmt.Errorf("wild-after-tame: a designated animal must no longer read as a tame candidate, got %#v", afterTameAnimal)
	}
	report["tame_dispatched"] = true

	// --- Release: the same immediate-write shape on the fixture's unbonded,
	// unmastered father muffalo. ---
	fatherRow, err := herdRow("father-before", fatherID)
	if err != nil {
		return err
	}
	fatherAnimal, _ := na.AsMap(fatherRow["animal"])
	if safe, _ := na.AsBool(fatherAnimal["safeToRelease"]); !safe {
		return fmt.Errorf("father-before: expected the fixture father to be safe to release, got %#v", fatherAnimal)
	}
	releaseEffect, err := applied("apply-release", "husbandry-release", fatherID, "RELEASE", nil)
	if err != nil {
		return err
	}
	if designated, _ := na.AsBool(releaseEffect["releaseDesignated"]); !designated {
		return fmt.Errorf("apply-release: expected a release designation, got %#v", releaseEffect)
	}
	afterReleaseRow, err := herdRow("father-after-release", fatherID)
	if err != nil {
		return err
	}
	afterReleaseAnimal, _ := na.AsMap(afterReleaseRow["animal"])
	if release, _ := na.AsBool(afterReleaseAnimal["release"]); !release {
		return fmt.Errorf("father-after-release: expected the release designation to be observable, got %#v", afterReleaseAnimal)
	}
	report["release_dispatched"] = true

	// --- Animals-tab settings: allowed area, master and following on the
	// obedient guard, read back through AnimalState. Each write's evidence
	// is the animal's actual post-write value, so a clear reads back empty. ---
	guardRow, err := herdRow("guard-before", guardID)
	if err != nil {
		return err
	}
	guardAnimal, _ := na.AsMap(guardRow["animal"])
	if obedient, _ := na.AsBool(guardAnimal["obedient"]); !obedient {
		return fmt.Errorf("guard-before: expected the fixture guard to read as obedient, got %#v", guardAnimal)
	}
	if supports, _ := na.AsBool(guardAnimal["supportsAllowedAreas"]); !supports {
		return fmt.Errorf("guard-before: expected the guard to support allowed areas, got %#v", guardAnimal)
	}
	if na.AsString(guardAnimal["allowedAreaId"]) != "" || na.AsString(guardAnimal["masterId"]) != "" {
		return fmt.Errorf("guard-before: expected no pre-existing area or master, got %#v", guardAnimal)
	}
	// The dog has not learned Obedience: master and following are refused
	// before any write, and the untrained dog reads as not obedient.
	dogNowRow, err := herdRow("dog-settings", dogID)
	if err != nil {
		return err
	}
	dogAnimal, _ := na.AsMap(dogNowRow["animal"])
	if obedient, _ := na.AsBool(dogAnimal["obedient"]); obedient {
		return fmt.Errorf("dog-settings: expected the untrained dog to read as not obedient, got %#v", dogAnimal)
	}
	if err := refused("master-untrained", "husbandry-master-untrained", dogID, "MASTER", map[string]any{"targetId": handlerID},
		"FAILURE_CODE_INVALID_REQUEST", "Husbandry refused: a master requires learned Obedience"); err != nil {
		return err
	}
	if err := refused("follow-untrained", "husbandry-follow-untrained", dogID, "FOLLOW_DRAFTED", map[string]any{"follow": true},
		"FAILURE_CODE_INVALID_REQUEST", "Husbandry refused: following requires learned Obedience"); err != nil {
		return err
	}
	// An area id the map does not carry is refused as not found.
	if err := refused("area-missing", "husbandry-area-missing", guardID, "ALLOWED_AREA", map[string]any{"targetId": "Area_Allowed_999999"},
		"FAILURE_CODE_NOT_FOUND", "Husbandry refused: allowed area Area_Allowed_999999 is not on the animal's map"); err != nil {
		return err
	}
	report["negative_settings_refusals"] = true

	// Area assign, then readback and clear.
	areaEffect, err := applied("apply-area", "husbandry-area", guardID, "ALLOWED_AREA", map[string]any{"targetId": areaID})
	if err != nil {
		return err
	}
	if na.AsString(areaEffect["allowedAreaId"]) != areaID {
		return fmt.Errorf("apply-area: expected allowedAreaId %s in the effect, got %#v", areaID, areaEffect)
	}
	afterAreaRow, err := herdRow("guard-after-area", guardID)
	if err != nil {
		return err
	}
	afterAreaAnimal, _ := na.AsMap(afterAreaRow["animal"])
	if na.AsString(afterAreaAnimal["allowedAreaId"]) != areaID {
		return fmt.Errorf("guard-after-area: expected the area to be observable, got %#v", afterAreaAnimal)
	}
	clearEffect, err := applied("apply-area-clear", "husbandry-area-clear", guardID, "ALLOWED_AREA", nil)
	if err != nil {
		return err
	}
	if v, present := clearEffect["allowedAreaId"]; !present || na.AsString(v) != "" {
		return fmt.Errorf("apply-area-clear: expected an empty allowedAreaId in the effect, got %#v", clearEffect)
	}
	afterClearRow, err := herdRow("guard-after-area-clear", guardID)
	if err != nil {
		return err
	}
	afterClearAnimal, _ := na.AsMap(afterClearRow["animal"])
	if na.AsString(afterClearAnimal["allowedAreaId"]) != "" {
		return fmt.Errorf("guard-after-area-clear: expected no area after clearing, got %#v", afterClearAnimal)
	}
	report["area_dispatched"] = true

	// Master: the handler becomes the guard's master.
	masterEffect, err := applied("apply-master", "husbandry-master", guardID, "MASTER", map[string]any{"targetId": handlerID})
	if err != nil {
		return err
	}
	if na.AsString(masterEffect["masterId"]) != handlerID {
		return fmt.Errorf("apply-master: expected masterId %s in the effect, got %#v", handlerID, masterEffect)
	}
	afterMasterRow, err := herdRow("guard-after-master", guardID)
	if err != nil {
		return err
	}
	afterMasterAnimal, _ := na.AsMap(afterMasterRow["animal"])
	if na.AsString(afterMasterAnimal["masterId"]) != handlerID {
		return fmt.Errorf("guard-after-master: expected the master to be observable, got %#v", afterMasterAnimal)
	}
	report["master_dispatched"] = true

	// Following: each flag is written on its own and the other is untouched.
	draftedEffect, err := applied("apply-follow-drafted", "husbandry-follow-drafted", guardID, "FOLLOW_DRAFTED", map[string]any{"follow": true})
	if err != nil {
		return err
	}
	if drafted, _ := na.AsBool(draftedEffect["followDrafted"]); !drafted {
		return fmt.Errorf("apply-follow-drafted: expected followDrafted in the effect, got %#v", draftedEffect)
	}
	afterDraftedRow, err := herdRow("guard-after-follow-drafted", guardID)
	if err != nil {
		return err
	}
	afterDraftedAnimal, _ := na.AsMap(afterDraftedRow["animal"])
	if drafted, _ := na.AsBool(afterDraftedAnimal["followDrafted"]); !drafted {
		return fmt.Errorf("guard-after-follow-drafted: expected followDrafted to be observable, got %#v", afterDraftedAnimal)
	}
	if fieldwork, _ := na.AsBool(afterDraftedAnimal["followFieldwork"]); fieldwork {
		return fmt.Errorf("guard-after-follow-drafted: followFieldwork must be untouched, got %#v", afterDraftedAnimal)
	}
	fieldworkEffect, err := applied("apply-follow-fieldwork", "husbandry-follow-fieldwork", guardID, "FOLLOW_FIELDWORK", map[string]any{"follow": true})
	if err != nil {
		return err
	}
	if fieldwork, _ := na.AsBool(fieldworkEffect["followFieldwork"]); !fieldwork {
		return fmt.Errorf("apply-follow-fieldwork: expected followFieldwork in the effect, got %#v", fieldworkEffect)
	}
	afterFieldworkRow, err := herdRow("guard-after-follow-fieldwork", guardID)
	if err != nil {
		return err
	}
	afterFieldworkAnimal, _ := na.AsMap(afterFieldworkRow["animal"])
	drafted, _ := na.AsBool(afterFieldworkAnimal["followDrafted"])
	fieldwork, _ := na.AsBool(afterFieldworkAnimal["followFieldwork"])
	if !drafted || !fieldwork {
		return fmt.Errorf("guard-after-follow-fieldwork: expected both follow flags set, got %#v", afterFieldworkAnimal)
	}
	report["following_dispatched"] = true

	// Standing removals are reversible settings: cancel each, read native
	// state back, and cancel again, which applies as it stands.
	for _, removal := range []struct{ id, order, field string }{
		{cowID, "CANCEL_SLAUGHTER", "slaughter"},
		{fatherID, "CANCEL_RELEASE", "release"},
	} {
		for _, label := range []string{"cancel-" + removal.field, "recancel-" + removal.field} {
			effect, err := applied(label, "husbandry-"+label, removal.id, removal.order, nil)
			if err != nil {
				return err
			}
			if designated, known := na.AsBool(effect[removal.field+"Designated"]); !known || designated {
				return fmt.Errorf("%s: cancellation effect %#v", label, effect)
			}
		}
		row, err := herdRow("after-cancel-"+removal.field, removal.id)
		if err != nil {
			return err
		}
		animal, _ := na.AsMap(row["animal"])
		if designated, known := na.AsBool(animal[removal.field]); !known || designated {
			return fmt.Errorf("cancellation native readback: %#v", animal)
		}
		report["cancel_"+removal.field] = animal
	}

	return nil
}
