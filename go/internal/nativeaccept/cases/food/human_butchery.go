package food

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "food/human-butchery", Scope: "Six raider corpses on EmptyChannels: pure trait/precept gate, screened stockpile, second worker-pinned human bill, actual herd kibble ingestion, protected survival-meal trade surplus and ordinary meal exclusion. Other colonists retain native butchery penalties.", Start: EmptyChannels("MealSurvivalPack", 0), RequiredOps: []string{"test/human_butchery_prepare", "test/human_butchery_observe"}, Budget: 5 * time.Minute, Stall: 90 * time.Second, Run: humanButchery})
}

func humanButchery(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := h.Call(ctx, "human-prepare", "test/human_butchery_prepare", nil)
	if err != nil {
		return err
	}
	s.Report()["fixture"] = prepared
	read := func() (observation.ColonyProjection, *o.ColonyFactsSnapshot, error) {
		reply, e := h.Wire(ctx, "human-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "planning": true})
		if e != nil {
			return observation.ColonyProjection{}, nil, e
		}
		raw, e := json.Marshal(reply)
		if e != nil {
			return observation.ColonyProjection{}, nil, e
		}
		facts := &o.ColonyFactsReply{}
		if e = protojson.Unmarshal(raw, facts); e != nil {
			return observation.ColonyProjection{}, nil, e
		}
		v := facts.GetObserved()
		if v == nil {
			return observation.ColonyProjection{}, nil, fmt.Errorf("food facts unavailable: %v", facts)
		}
		id := v.Context.Identity
		p, e := observation.DecodeColony(facts, observation.Identity{Colony: domain.ColonyID(id.GetColonyId()), Map: domain.MapID(id.GetMapId()), Load: domain.LoadID(id.GetLoadToken()), Tick: domain.Tick(v.Context.GetTick())})
		return p, v, e
	}
	if _, err = na.GrantAuto(ctx, h.WireFunc(), "human-grant", s.Identity()); err != nil {
		return err
	}
	execute := func(label string, operation *op.Operation) error {
		_, generation, e := na.AuthorityStatus(ctx, h.WireFunc(), label+"-authority", s.Identity())
		if e != nil {
			return e
		}
		raw, e := protojson.Marshal(operation)
		if e != nil {
			return e
		}
		var payload map[string]any
		if e = json.Unmarshal(raw, &payload); e != nil {
			return e
		}
		reply, e := h.Wire(ctx, label, "operations_execute", map[string]any{"precondition": map[string]any{"identity": s.Identity(), "expectedGeneration": fmt.Sprint(generation), "attempt": map[string]any{"controllerSessionId": "human-butchery", "actionId": label, "attemptId": "1"}}, "operation": payload})
		if e != nil {
			return e
		}
		_, receipt, e := na.Outcome(reply, "receipt")
		if e != nil {
			return e
		}
		if _, ok := na.AsMap(receipt["applied"]); !ok {
			return fmt.Errorf("%s refused: %v", label, receipt)
		}
		return nil
	}
	p, v, err := read()
	if err != nil {
		return err
	}
	initial, complete := p.CombinedFoodSupply.Value()
	if !complete {
		return fmt.Errorf("food census unavailable")
	}
	corpses := 0
	for _, stock := range initial.Stocks {
		if stock.Corpse && stock.IsHumanlike {
			corpses++
		}
	}
	if corpses != 6 {
		return fmt.Errorf("want six humanlike corpse rows, got %d", corpses)
	}
	selected, ok := policy.SelectHumanButcher(p.ProductionBenches)
	if !ok || selected.Worker != na.AsString(prepared["cook"]) {
		return fmt.Errorf("qualified psychopath not selected: %+v", selected)
	}
	rows, _ := p.ProductionBenches.Value()
	var chosen policy.ProductionBench
	for _, b := range rows {
		if b.ID == selected.Bench {
			chosen = b
		}
	}
	if len(chosen.HumanStorageCells) != 6 {
		return fmt.Errorf("no screened six-cell corpse stockpile: %+v", chosen)
	}
	zone, err := domain.NewAllowListStockpileZone(domain.ImportantPriority, []string{chosen.HumanCorpseDef}, chosen.HumanStorageCells)
	if err != nil {
		return err
	}
	command := bridge.ZoneConfiguration(zone)
	token := v.GetPlanning().GetObserved().GetZoneMapSnapshot().GetToken()
	command.ExpectedMapSnapshotToken = &token
	if err = execute("human-storage", &op.Operation{Command: &op.Operation_CreateZone{CreateZone: command}}); err != nil {
		return err
	}
	p, _, err = read()
	if err != nil {
		return err
	}
	selected, ok = policy.SelectHumanButcher(p.ProductionBenches)
	if !ok {
		return fmt.Errorf("human selection lost after stockpile")
	}
	rows, _ = p.ProductionBenches.Value()
	ready := false
	for _, b := range rows {
		if b.ID == selected.Bench {
			ready, _ = b.HumanStorageReady.Value()
		}
	}
	if !ready {
		return fmt.Errorf("human corpse stockpile is not screened")
	}
	bill, err := domain.NewHumanButcherBill(selected.Bench, selected.Token, selected.Worker)
	if err != nil {
		return err
	}
	if err = execute("human-bill", bridge.BillOperation(bill)); err != nil {
		return err
	}
	add := func(label, bench, recipe string, target int32, ingredients ...string) error {
		p, _, e := read()
		if e != nil {
			return e
		}
		benches, _ := p.ProductionBenches.Value()
		for _, b := range benches {
			if b.ID == bench {
				token, k := b.Token.Value()
				if !k {
					return fmt.Errorf("missing bench token")
				}
				bill, e := domain.NewProductionBill(bench, recipe, token, domain.StockTarget, target, ingredients...)
				if e != nil {
					return e
				}
				return execute(label, bridge.BillOperation(bill))
			}
		}
		return fmt.Errorf("missing bench %s", bench)
	}
	if err = add("ordinary-meal", na.AsString(prepared["stove"]), "CookMealSimple", 1); err != nil {
		return err
	}
	if err = add("human-kibble", na.AsString(prepared["feedBench"]), "Make_Kibble", 40, "Meat_Human", "RawRice"); err != nil {
		return err
	}
	saleAdded := false
	for tick := 0; tick < 30000; tick += 500 {
		if _, err = s.Advance(ctx, 500); err != nil {
			return err
		}
		p, _, err = read()
		if err != nil {
			return err
		}
		supply, sk := p.CombinedFoodSupply.Value()
		human, hk := p.FoodSupply.Value()
		benches, bk := p.ProductionBenches.Value()
		if !sk || !hk || !bk {
			return fmt.Errorf("human food projection unavailable")
		}
		var ids []policy.PawnID
		for _, c := range human.Consumers {
			ids = append(ids, c.ID)
		}
		if channel, ok := policy.HumanFoodChannel(benches, supply, ids, 3); ok {
			plan := domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: channel, Decision: policy.FoodPlanHold, Terms: channel.Terms}}})
			s.Report()["routing"] = channel.Terms
			if !saleAdded {
				if sale, ok := policy.SelectHumanSurvivalBill(p.ProductionBenches, supply, plan); ok {
					bill, e := domain.NewProductionBill(sale.Bench, sale.Recipe, sale.Token, sale.Mode, sale.Target, sale.Ingredients...)
					if e != nil {
						return e
					}
					if e = execute("human-survival", bridge.BillOperation(bill)); e != nil {
						return e
					}
					saleAdded = true
				}
			}
		}
		audit, e := h.Call(ctx, "human-audit", "test/human_butchery_observe", nil)
		if e != nil {
			return e
		}
		s.Report()["after"] = audit
		if audit["unsafeMealBill"] == true || audit["personalPenalty"] == true {
			return fmt.Errorf("human food guard violated: %v", audit)
		}
		if audit["pinned"] == true && audit["animalBill"] == true && len(na.AsSlice(audit["fedHerd"])) == 2 && na.AsNumber(audit["tradeMeals"]) > 0 {
			return nil
		}
	}
	return fmt.Errorf("human butchery did not feed the herd and produce protected trade surplus within 30000 ticks")
}
