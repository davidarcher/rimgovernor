// The caravan/departure case exercises the FormCaravanIntent arm of
// Actions/Apply (#942) end to end against a live game: the native caravan
// catalog with its food facts (NativeCaravanCatalog.cs, #464), the pack a
// planner composes from them (policy.PlanCaravanCargo: WoodLog trade cargo
// plus the crew's journey food, reserve first, simple meals left home), the
// intent applied through rimgovernor/operations_apply, and finally native's
// own caravan inventory census, which must carry exactly the composed pack.
// Uses a private disposable fixture (test/caravan_departure_prepare) to
// guarantee a colony shaped for it (leave >=1 home colonist, the routine
// food floor kept at home) with a forbidden pemmican reserve, survival
// meals, simple meals and WoodLog cargo, and a real reachable destination.
package caravan

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name: "caravan/departure",
		Scope: "Native FormCaravanIntent: catalog food facts, a pack composed reserve-first (pemmican, " +
			"then survival meals, simple meals left home) over the routine home food floor, an actual " +
			"formation through Actions/Apply, native's caravan inventory carrying exactly that pack, " +
			"native's home-staffing refusal, replay and a departed crew applied again.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/caravan_departure_prepare", Args: map[string]any{"crewCount": 1}},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runDeparture,
	})
}

func runDeparture(ctx context.Context, s cases.Session) error {
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	if !na.Contains(names, "rimgovernor/observations_read_caravan_catalog") {
		return fmt.Errorf("missing rimgovernor/observations_read_caravan_catalog in discovery")
	}

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

	// The catalog's food facts, the pack is composed from.
	catalog, _, err := h.Client.ReadCaravanCatalog(ctx, id, int32(destinationTile))
	if err != nil {
		return fmt.Errorf("catalog-before: %w", err)
	}
	// One definition may span several groups (RimWorld splits stacks by
	// hit points, ingredients or rot stage); fold them per definition,
	// keeping the reserve group's id so the pack order can be checked.
	groups := map[string]policy.CaravanCargoGroup{}
	// Unforbidden groups fold apart too: the start may hold a forbidden
	// stack of survival meals beside the fixture's unforbidden 60 (#717).
	open := map[string]policy.CaravanCargoGroup{}
	for _, group := range catalog.CargoGroups {
		nutrition := domain.Unknown[float64]()
		if group.Nutrition != nil {
			nutrition = domain.Known(group.GetNutrition())
		}
		rot := domain.Unknown[float64]()
		if group.RotDays != nil {
			rot = domain.Known(group.GetRotDays())
		}
		row := policy.CaravanCargoGroup{GroupID: group.GetGroupId(), Definition: group.GetDefName(), Count: group.GetCount(), Nutrition: nutrition, Perishable: group.GetPerishable(), RotDays: rot, Reserve: group.GetReserve()}
		for _, eater := range group.EaterIds {
			row.Eaters = append(row.Eaters, domain.PawnID(eater))
		}
		if !row.Reserve {
			folded := row
			if prior, seen := open[row.Definition]; seen {
				folded.Count += prior.Count
				folded.Perishable = folded.Perishable || prior.Perishable
				folded.Eaters = append(folded.Eaters, prior.Eaters...)
			}
			open[row.Definition] = folded
		}
		if prior, seen := groups[row.Definition]; seen {
			row.Count += prior.Count
			row.Reserve = row.Reserve || prior.Reserve
			row.Perishable = row.Perishable || prior.Perishable
			if !row.Reserve || prior.Reserve {
				row.GroupID = prior.GroupID
			}
			if prior.Reserve {
				row.RotDays = prior.RotDays
			}
			row.Eaters = append(row.Eaters, prior.Eaters...)
		}
		groups[row.Definition] = row
	}
	pemmican, survival, meals, wood := groups["Pemmican"], open["MealSurvivalPack"], groups["MealSimple"], groups["WoodLog"]
	if wood.Count < 10 {
		return fmt.Errorf("catalog-before: expected a WoodLog cargo group with at least 10 available, got %#v", groups)
	}
	if _, known := wood.Nutrition.Value(); known {
		return fmt.Errorf("catalog-before: WoodLog carries a nutrition fact: %#v", wood)
	}
	crewEats := func(g policy.CaravanCargoGroup) bool {
		for _, eater := range g.Eaters {
			if string(eater) == crewPawnIDs[0] {
				return true
			}
		}
		return false
	}
	if n, known := pemmican.Nutrition.Value(); pemmican.Count != 20 || !known || n <= 0 || !pemmican.Reserve || !pemmican.Perishable || !crewEats(pemmican) {
		return fmt.Errorf("catalog-before: expected the forbidden pemmican stack as a crew-eligible perishable reserve group, got %#v", pemmican)
	}
	if rot, known := pemmican.RotDays.Value(); !known || rot < 30 {
		return fmt.Errorf("catalog-before: expected pemmican's unrefrigerated shelf life (>=30 days), got %#v", pemmican)
	}
	// The start may hold survival meals of its own beside the fixture's 60.
	if n, known := survival.Nutrition.Value(); survival.Count < 60 || !known || n <= 0 || survival.Reserve || survival.Perishable || !crewEats(survival) {
		return fmt.Errorf("catalog-before: expected at least 60 unforbidden non-perishable survival meals, got %#v", survival)
	}
	if rot, known := meals.RotDays.Value(); meals.Count < 5 || meals.Reserve || !meals.Perishable || !known || rot <= 0 || rot > 5 || !crewEats(meals) {
		return fmt.Errorf("catalog-before: expected perishable simple meals with a few days of shelf life, got %#v", meals)
	}

	// Compose the pack the way a planner would (policy.PlanCaravanCargo,
	// #464): the WoodLog trade cargo plus the crew's journey food, reserve
	// first, with the routine food floor kept at home.
	colony, _, err := h.Client.ReadColonyFacts(ctx, id, false, nil)
	if err != nil {
		return fmt.Errorf("colony-facts: %w", err)
	}
	demand := map[domain.PawnID]float64{}
	for _, consumer := range colony.GetObserved().GetFoodSupply().GetObserved().GetConsumers() {
		if consumer.NutritionPerDay != nil {
			demand[domain.PawnID(consumer.GetPawnId())] = consumer.GetNutritionPerDay()
		}
	}
	journeyDays := 0.0
	for _, route := range catalog.Routes {
		if route.GetDestination() == int32(destinationTile) && route.EstimatedTicks != nil {
			journeyDays = float64(route.GetEstimatedTicks())/60000 + 1
		}
	}
	if journeyDays == 0 {
		return fmt.Errorf("catalog-before: no travel estimate to the destination: %+v", catalog.Routes)
	}
	var rows []policy.CaravanCargoGroup
	for _, group := range catalog.CargoGroups {
		nutrition := domain.Unknown[float64]()
		if group.Nutrition != nil {
			nutrition = domain.Known(group.GetNutrition())
		}
		rot := domain.Unknown[float64]()
		if group.RotDays != nil {
			rot = domain.Known(group.GetRotDays())
		}
		row := policy.CaravanCargoGroup{GroupID: group.GetGroupId(), Definition: group.GetDefName(), Count: group.GetCount(), Nutrition: nutrition, Perishable: group.GetPerishable(), RotDays: rot, Reserve: group.GetReserve()}
		for _, eater := range group.EaterIds {
			row.Eaters = append(row.Eaters, domain.PawnID(eater))
		}
		rows = append(rows, row)
	}
	floor := policy.DefaultRoutinePolicy().FoodMinDays
	pack, refusal := policy.PlanCaravanCargo(policy.CaravanCargoRequest{Crew: []domain.PawnID{domain.PawnID(crewPawnIDs[0])}, Cargo: []domain.CargoItem{{Definition: "WoodLog", Count: 10}}, Groups: rows, Demand: demand, JourneyDays: journeyDays, HomeFoodMinDays: floor})
	if refusal != "" {
		return fmt.Errorf("pack: refused %s (demand %v, journey %.1f days)", refusal, demand, journeyDays)
	}
	if pack.HomeRunwayDays < floor {
		return fmt.Errorf("pack: home food floor %v not kept: runway %v", floor, pack.HomeRunwayDays)
	}
	if len(pack.Food) == 0 || pack.Food[0].GroupID != pemmican.GroupID {
		return fmt.Errorf("pack: expected the pemmican reserve packed first, got %+v", pack.Food)
	}
	packed := map[string]int64{}
	for _, line := range pack.Cargo {
		packed[line.Definition] += line.Count
	}
	if packed["WoodLog"] != 10 || packed["MealSimple"] != 0 || packed["Pemmican"] == 0 {
		return fmt.Errorf("pack: unexpected pack %v (food %+v)", packed, pack.Food)
	}
	if packed["Pemmican"] < 20 && packed["MealSurvivalPack"] != 0 {
		return fmt.Errorf("pack: survival meals packed before the reserve was exhausted: %v", packed)
	}
	fmt.Fprintf(os.Stderr, "pack: %v, home runway %.1f days\n", packed, pack.HomeRunwayDays)
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

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
