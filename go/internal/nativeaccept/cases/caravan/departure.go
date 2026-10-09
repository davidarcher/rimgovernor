// The caravan/departure case exercises the FormCaravanIntent arm of
// Actions/Apply end to end against a live game: a fixed pack
// (WoodLog trade cargo, the forbidden pemmican reserve and survival meals,
// simple meals left home) applied through rimgovernor/operations_apply, and
// native's own caravan inventory census, which must carry exactly that pack.
// Uses a private disposable fixture (test/caravan_departure_prepare) to
// guarantee a colony shaped for it (leave >=1 home colonist) with a
// forbidden pemmican reserve, survival meals, simple meals and WoodLog
// cargo, and a real reachable destination.
package caravan

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "caravan/departure",
		Scope: "Native FormCaravanIntent: a fixed pack (WoodLog, the forbidden pemmican reserve, survival " +
			"meals, simple meals left home) formed through Actions/Apply, native's caravan inventory " +
			"carrying exactly that pack, native's home-staffing refusal, replay and a departed crew applied again.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/caravan_departure_prepare", Args: map[string]any{"crewCount": 1}},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runDeparture,
	})
}

func runDeparture(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()

	var crewPawnIDs, remainingPawnIDs []string
	for _, raw := range na.AsSlice(prepared["crewPawnIds"]) {
		crewPawnIDs = append(crewPawnIDs, fmt.Sprint(raw))
	}
	for _, raw := range na.AsSlice(prepared["remainingPawnIds"]) {
		remainingPawnIDs = append(remainingPawnIDs, fmt.Sprint(raw))
	}
	destinationTile := na.AsNumber(prepared["destinationTile"])
	if len(crewPawnIDs) != 1 || len(remainingPawnIDs) < 2 || destinationTile <= 0 {
		return fmt.Errorf("caravan_departure_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	identityData, err := json.Marshal(identity)
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(identityData, id); err != nil {
		return err
	}

	// The fixture's whole forbidden pemmican stack (20) plus enough survival
	// meals for native's one-day food floor; simple meals stay home.
	packed := map[string]int64{"WoodLog": 10, "Pemmican": 20, "MealSurvivalPack": 10}
	var cargo []map[string]any
	for def, count := range packed {
		cargo = append(cargo, map[string]any{"defName": def, "count": count})
	}

	apply := func(label, key string, pawnIDs []string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{
			map[string]any{"key": key, "formCaravan": map[string]any{"pawnIds": pawnIDs, "cargo": cargo, "destinationTile": destinationTile}},
		}})
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
	formed := func(label string, result map[string]any) error {
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, _ := na.AsMap(observed["caravan"])
		if started, _ := na.AsBool(effect["assemblyStarted"]); !started || na.AsNumber(effect["destinationTile"]) != destinationTile {
			return fmt.Errorf("%s: expected an applied formation toward the destination, got %#v", label, result)
		}
		return nil
	}

	// Refusal: native keeps at least one colonist home.
	allPawnIDs := append(append([]string{}, crewPawnIDs...), remainingPawnIDs...)
	result, err := apply("leave-no-one-home", "caravan-departure-everyone", allPawnIDs)
	if err != nil {
		return err
	}
	if refusal, _ := na.AsMap(result["refused"]); refusal == nil || na.AsString(refusal["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("leave-no-one-home: expected an invalid-request refusal, got %#v", result)
	}

	// Apply: the real native Dialog_FormCaravan mechanism
	// (TryFormAndSendCaravan) with exactly the composed pack.
	if result, err = apply("apply", "caravan-departure", crewPawnIDs); err != nil {
		return err
	}
	if err = formed("apply", result); err != nil {
		return err
	}
	// Replay: the same key returns the identical result.
	replay, err := apply("replay", "caravan-departure", crewPawnIDs)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, result) {
		return fmt.Errorf("replay: the same key returned a different result: %#v", replay)
	}

	// The crew gathers the pack and walks to the exit in game time, so the
	// wait is a tick budget (RunUntil runs and re-pauses the clock).
	var caravan bridge.CaravanJourney
	if _, err := na.RunUntil(ctx, h, "observe-formation", na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		world, _, err := h.Client.ReadWorldProgression(ctx, id, false)
		if err != nil {
			return "", false, err
		}
		for _, row := range world.Caravans {
			if len(row.PawnIDs) == 1 && row.PawnIDs[0] == crewPawnIDs[0] {
				caravan = row
				return row.ID, true, nil
			}
		}
		return fmt.Sprintf("%d caravans", len(world.Caravans)), false, nil
	}); err != nil {
		return fmt.Errorf("observe: caravan formation did not complete: %w", err)
	}
	// Native's own caravan census carries exactly the composed pack: the
	// reserve and survival meals aboard, every simple meal left home.
	for _, def := range []string{"WoodLog", "Pemmican", "MealSurvivalPack", "MealSimple"} {
		if caravan.Inventory[def] != packed[def] {
			return fmt.Errorf("world-progression: caravan carries %s x%d, the pack had x%d (inventory %v)", def, caravan.Inventory[def], packed[def], caravan.Inventory)
		}
	}

	// A fresh key after the crew departed is applied again: the intent
	// already holds, so a resend after a lost reply cannot form twice.
	again, err := apply("apply-again", "caravan-departure-again", crewPawnIDs)
	if err != nil {
		return err
	}
	if err = formed("apply-again", again); err != nil {
		return err
	}

	return nil
}
