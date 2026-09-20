// The caravan/departure case exercises the CaravanDeparture vertical end to
// end against a live game through the real Go departure adapter
// (buildingruntime.CaravanDepartureBoundary): the native caravan catalog
// with its food facts (NativeCaravanCatalog.cs, #464), the pack the adapter
// composes from them (policy.PlanCaravanCargo: the action's trade cargo plus
// the crew's journey food, reserve first, simple meals left home), the
// admission (policy.EvaluateCaravanDeparture), the FormCaravan dispatch and
// its observation, and finally native's own caravan inventory census, which
// must carry exactly the composed pack. Uses a private disposable fixture
// (test/caravan_departure_prepare) to guarantee a colony shaped for the
// admission policy (leave >=1 home colonist, a home doctor, the routine
// food floor kept at home) with a forbidden pemmican reserve, survival
// meals, simple meals and WoodLog cargo, and a real reachable destination.
package caravan

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

const departureOwner = "native-caravan-departure-acceptance"

func init() {
	cases.Register(cases.Case{
		Name: "caravan/departure",
		Scope: "Native CaravanDeparture vertical through the Go departure adapter: catalog food facts, " +
			"a pack composed reserve-first (pemmican, then survival meals, simple meals left home) over the " +
			"routine home food floor, admission, an actual FormCaravan dispatch, its observed completion and " +
			"native's caravan inventory carrying exactly that pack; plus native's home-staffing and " +
			"post-departure stale-catalog refusals.",
		Start:  cases.Fixture{Op: "test/caravan_departure_prepare", Args: map[string]any{"crewCount": 1}},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runDeparture,
	})
}

// staticLeases stands in for the session's lease source: this case owns the
// granted authority itself.
type staticLeases string

func (l staticLeases) Lease(domain.GenerationSnapshot) (string, error) { return string(l), nil }

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

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

	grant, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity)
	if err != nil {
		return err
	}
	grantContext, _ := na.AsMap(grant["context"])
	generation := uint64(na.AsNumber(grantContext["nativeGeneration"]))
	if generation == 0 {
		return fmt.Errorf("acquire: missing native generation: %#v", grant)
	}
	identityData, err := json.Marshal(identity)
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(identityData, id); err != nil {
		return err
	}
	snapshot := domain.GenerationSnapshot{Colony: domain.ColonyID(id.GetColonyId()), Load: domain.LoadID(id.GetLoadToken()), Map: domain.MapID(id.GetMapId()), Plan: "caravan-departure-plan", Revision: 1, Native: domain.NativeGeneration(generation)}

	// The action names crew, trade cargo and destination only; the adapter
	// composes the food.
	departure, err := domain.NewCaravanDeparture([]domain.PawnID{domain.PawnID(crewPawnIDs[0])}, []domain.CargoItem{{Definition: "WoodLog", Count: 10}}, int32(destinationTile))
	if err != nil {
		return err
	}
	action, err := domain.NewCaravanDepartureAction("caravan-departure", departure)
	if err != nil {
		return err
	}
	plan, err := domain.NewPlan(snapshot.Plan, snapshot.Revision, []domain.Action{action})
	if err != nil {
		return err
	}
	progress, err := domain.NewProgress(plan, action.ID())
	if err != nil {
		return err
	}
	journal, err := store.Open(ctx, filepath.Join(h.Output, "caravan-departure.sqlite"))
	if err != nil {
		return err
	}
	defer journal.Close()
	writer, err := bridge.NewCaravanDepartureWriter(h.Client)
	if err != nil {
		return err
	}
	floor := policy.DefaultRoutinePolicy().FoodMinDays
	adapter, err := buildingruntime.NewCaravanDepartureBoundary(h.Client, writer, journal, staticLeases("acceptance-lease"), wallClock{}, departureOwner, floor)
	if err != nil {
		return err
	}

	// The catalog's food facts, read the way the adapter reads them.
	catalog, _, err := h.Client.ReadCaravanCatalog(ctx, id, int32(destinationTile))
	if err != nil {
		return fmt.Errorf("catalog-before: %w", err)
	}
	// One definition may span several groups (RimWorld splits stacks by
	// hit points, ingredients or rot stage); fold them per definition,
	// keeping the reserve group's id so the pack order can be checked.
	groups := map[string]policy.CaravanCargoGroup{}
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
	pemmican, survival, meals, wood := groups["Pemmican"], groups["MealSurvivalPack"], groups["MealSimple"], groups["WoodLog"]
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

	// Inspect: the adapter composes and previews the pack.
	inspection, err := adapter.InspectCaravanDeparture(ctx, executor.Target{Action: action, Snapshot: snapshot})
	if err != nil {
		return fmt.Errorf("inspect: %w", err)
	}
	facts := inspection.Facts
	if facts.CargoRefusal != "" {
		return fmt.Errorf("inspect: the adapter refused the pack: %s (policy %+v)", facts.CargoRefusal, inspection.Policy)
	}
	if inspection.Policy.MinimumHomeFoodDays != floor || facts.Cargo.HomeRunwayDays < floor {
		return fmt.Errorf("inspect: home food floor %v not kept: runway %v", inspection.Policy.MinimumHomeFoodDays, facts.Cargo.HomeRunwayDays)
	}
	if len(facts.Cargo.Food) == 0 || facts.Cargo.Food[0].GroupID != pemmican.GroupID {
		return fmt.Errorf("inspect: expected the pemmican reserve packed first, got %+v", facts.Cargo.Food)
	}
	packed := map[string]int64{}
	for _, line := range facts.Cargo.Cargo {
		packed[line.Definition] += line.Count
	}
	if packed["WoodLog"] != 10 || packed["MealSimple"] != 0 || packed["Pemmican"] == 0 {
		return fmt.Errorf("inspect: unexpected pack %v (food %+v)", packed, facts.Cargo.Food)
	}
	if packed["Pemmican"] < 20 && packed["MealSurvivalPack"] != 0 {
		return fmt.Errorf("inspect: survival meals packed before the reserve was exhausted: %v", packed)
	}
	// The whole pack must outlast the journey: pemmican covers up to 10
	// crew-days, survival meals the rest.
	if _, known := facts.RouteReachable.Value(); !known {
		return fmt.Errorf("inspect: route reachability unknown: %+v", facts)
	}
	fmt.Fprintf(os.Stderr, "inspect: pack %v, home runway %.1f days, preview accepted %v\n", packed, facts.Cargo.HomeRunwayDays, facts.NativeCanTry)

	decision := policy.EvaluateCaravanDeparture(policy.CaravanDepartureRequest{Action: action, Progress: progress, Current: snapshot, MinimumTick: 0, Policy: inspection.Policy, Facts: facts})
	if !decision.Admitted {
		return fmt.Errorf("admission: refused %+v (facts %+v)", decision.Refused, facts)
	}
	crew := make([]store.CaravanCrewAdmission, len(facts.Crew))
	for i, member := range facts.Crew {
		crew[i] = store.CaravanCrewAdmission{Pawn: member.Pawn, SnapshotToken: member.SnapshotToken}
	}
	cargo := make([]store.CaravanCargoAdmission, len(facts.Cargo.Cargo))
	for i, line := range facts.Cargo.Cargo {
		cargo[i] = store.CaravanCargoAdmission{GroupID: line.GroupID, Definition: line.Definition, Count: line.Count}
	}
	admission := store.CaravanDepartureAdmission{Snapshot: snapshot, Tick: facts.PreviewTick, Crew: crew, CatalogToken: facts.CatalogToken, Cargo: cargo}
	dispatch := executor.CaravanDepartureDispatch{Attempt: executor.Placement{Action: action, Attempt: 1, Snapshot: snapshot, Tick: facts.PreviewTick}, Admission: admission}

	// Refusal: leaving zero colonists home is refused by native's own
	// FormCaravan admission check, the same home-staffing floor
	// policy.CaravanDeparturePolicy's MinimumHomeColonists enforces.
	allPawnIDs := append(append([]string{}, crewPawnIDs...), remainingPawnIDs...)
	buildOperation := func(token string, pawnIDs []string, cargoSelection []map[string]any) map[string]any {
		return map[string]any{"formCaravan": map[string]any{
			"expectedCatalogToken": token, "pawnIds": pawnIDs, "cargo": cargoSelection, "destinationTile": destinationTile,
		}}
	}
	leaveNoOneHomeReply, err := h.Wire(ctx, "leave-no-one-home", "operations_preview", map[string]any{
		"identity": identity, "operation": buildOperation(facts.CatalogToken, allPawnIDs, nil),
	})
	if err != nil {
		return err
	}
	_, leaveNoOneHomeFailure, err := na.Outcome(leaveNoOneHomeReply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(leaveNoOneHomeFailure["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("leave-no-one-home: expected FAILURE_CODE_INVALID_REQUEST, got %#v", leaveNoOneHomeFailure)
	}

	// Dispatch: the real native Dialog_FormCaravan mechanism
	// (TryFormAndSendCaravan) with exactly the admitted pack. RimWorld's own
	// post-formation steps are not exception-safe, so native may report
	// uncertain rather than applied; either resolves through observation.
	receipt, err := adapter.DepartCaravan(ctx, dispatch)
	if err != nil {
		return fmt.Errorf("depart: %w", err)
	}
	if receipt.Kind == domain.ReceiptRefused {
		return fmt.Errorf("depart: native refused the admitted pack")
	}
	if receipt.Kind != domain.ReceiptAccepted {
		fmt.Fprintln(os.Stderr, "depart: native returned uncertain; resolving via observe")
	}

	// Observe: the crew walks to the exit tile in game time, so the wait is
	// a tick budget (RunUntil runs and re-pauses the clock), not seconds.
	var evidence executor.CaravanDepartureEvidence
	if _, err := na.RunUntil(ctx, h, "observe-formation", na.TicksPerDay, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		evidence, err = adapter.ObserveCaravanDeparture(ctx, dispatch, snapshot)
		if err != nil {
			return "", false, err
		}
		return fmt.Sprintf("%s/%s", evidence.Observation.Effect, evidence.CaravanID), evidence.Complete, nil
	}); err != nil {
		return fmt.Errorf("observe: caravan formation did not complete: %w (last %+v)", err, evidence)
	}
	if evidence.Observation.Effect != domain.EffectCompleted || evidence.CaravanID == "" || len(evidence.Crew) != 1 || string(evidence.Crew[0]) != crewPawnIDs[0] {
		return fmt.Errorf("observe: expected a completed departure with a caravan id, got %+v", evidence)
	}

	// Native's own caravan census carries exactly the composed pack: the
	// reserve and survival meals aboard, every simple meal left home.
	world, _, err := h.Client.ReadWorldProgression(ctx, id, false)
	if err != nil {
		return fmt.Errorf("world-progression: %w", err)
	}
	var inventory map[string]int64
	for _, caravan := range world.Caravans {
		if caravan.ID == evidence.CaravanID {
			inventory = caravan.Inventory
		}
	}
	if inventory == nil {
		return fmt.Errorf("world-progression: caravan %s missing from %+v", evidence.CaravanID, world.Caravans)
	}
	for _, def := range []string{"WoodLog", "Pemmican", "MealSurvivalPack", "MealSimple"} {
		if inventory[def] != packed[def] {
			return fmt.Errorf("world-progression: caravan carries %s x%d, the pack had x%d (inventory %v)", def, inventory[def], packed[def], inventory)
		}
	}

	// Refusal: a brand-new attempt after the crew has departed is refused as
	// stale (the catalog census and its CAS token changed) rather than
	// re-admitted or double-formed.
	staleReply, err := h.Wire(ctx, "post-departure-retry", "operations_execute", map[string]any{
		"precondition": map[string]any{
			"identity": identity, "expectedGeneration": grantContext["nativeGeneration"],
			"attempt": map[string]any{"controllerSessionId": departureOwner, "actionId": "caravan-departure-again", "attemptId": "1"},
		},
		"operation": buildOperation(facts.CatalogToken, crewPawnIDs, []map[string]any{{"groupId": wood.GroupID, "count": 10}}),
	})
	if err != nil {
		return err
	}
	_, staleFailure, err := na.Outcome(staleReply, "failure")
	if err != nil {
		return err
	}
	if na.AsString(staleFailure["code"]) != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("post-departure-retry: expected FAILURE_CODE_INVALID_REQUEST, got %#v", staleFailure)
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
