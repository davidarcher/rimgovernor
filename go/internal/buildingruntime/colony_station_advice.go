package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// stationAdvice runs the station chain over the live colony at a layout
// review and returns its advice, which the review reads for a successor room
// (policy.OutgrownSuccession). The chain is skipped, and the last advice kept,
// while its inputs are unchanged: the facts' key (StationFacts.InputsKey) and
// the survey, as for the replan. A run records one layout_stations decision.
// The advice is false when the chain failed or a fact is unread.
func (r *Rounder) stationAdvice(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, plan policy.LayoutPlan, survey policy.MapSurvey) (policy.StationAdvice, bool) {
	f := stationFactsOf(projection, plan, survey, r.demand.get(snapshot).Needs, r.WorkLedgerView())
	inputs := layoutInputs{key: fmt.Sprint(snapshot.Load, "|", f.InputsKey()), bounds: survey.Bounds, cells: survey.Cells}
	if inputs.same(r.stationInputs) {
		return r.stationLast, r.stationHeld
	}
	start := time.Now()
	facts, err := r.stationFacts(ctx, snapshot, f)
	decision, advice, ok := stationDecision(facts, err)
	decision.Dur = time.Since(start)
	telemetry.Decide(ctx, decision)
	r.stationInputs, r.stationLast, r.stationHeld = inputs, advice, ok
	return advice, ok
}

// stationDecision runs the chain over facts (when they were read, err nil) and
// is the layout_stations row of the outcome: advised, skipped for an unknown
// fact, or failed. The advice is returned when it was made.
func stationDecision(facts policy.StationFacts, err error) (telemetry.Decision, policy.StationAdvice, bool) {
	decision := telemetry.Decision{Kind: "layout_stations", Component: "layout", Target: "plan"}
	var advice policy.StationAdvice
	if err == nil {
		advice, err = policy.AdviseStations(facts)
	}
	switch {
	case errors.Is(err, policy.ErrStationFacts):
		decision.Verdict, decision.Reason, decision.Attrs = "skipped", "facts_unknown", map[string]any{"fact": err.Error()}
	case err != nil:
		decision.Level, decision.Verdict, decision.Reason, decision.Attrs = slog.LevelWarn, "failed", "chain_error", map[string]any{"error": err}
	default:
		decision.Verdict, decision.Reason, decision.Attrs = "advised", "station_chain", stationAdviceAttrs(advice)
		return decision, advice, true
	}
	return decision, policy.StationAdvice{}, false
}

// stationFacts adds the catalog rows (recipe flows, stack limits, rot) to f,
// the facts the colony read. A part the colony has not read stays unknown.
func (r *Rounder) stationFacts(ctx context.Context, snapshot domain.GenerationSnapshot, f policy.StationFacts) (policy.StationFacts, error) {
	source, ok := r.native.(observation.DefinitionSource)
	if !ok {
		return policy.StationFacts{}, errors.New("station advice: no definitions")
	}
	catalog, err := source.DefinitionCatalog(ctx, controlIdentity(snapshot))
	if err != nil {
		return policy.StationFacts{}, err
	}
	if f.Recipes, err = catalog.StationRecipeFlows(f.Catalog); err != nil {
		return policy.StationFacts{}, err
	}
	stock, _ := f.Stock.Value()
	f.StackLimit, f.Perishable, err = catalog.StationItemFacts(stock, f.Recipes)
	return f, err
}

// stationFactsOf is every station fact the projection, the resource demand and
// the work ledger hold; the catalog rows (recipe flows, stack limits, rot) are
// added by the caller.
func stationFactsOf(projection observation.ColonyProjection, plan policy.LayoutPlan, survey policy.MapSurvey, needs map[policy.Resource]int64, ledger policy.LedgerView) policy.StationFacts {
	f := policy.StationFacts{
		Catalog: projection.Stations, Furniture: projection.Shapes.Furniture, Items: projection.Facts.Items,
		Colonists: projection.Facts.Colonists, Patients: projection.Facts.MedicalPawns,
		Surgical:  policy.SurgeryBedShortPatients(projection.Facts.MedicalPawns),
		Prisoners: domain.Unknown[int](), Animals: projection.Facts.PenAnimals(),
		WorkPawns: projection.WorkPawns, Food: projection.FoodSupply,
		Orders: domain.Unknown[[]policy.OrderSpec](), Stock: domain.Unknown[map[policy.Resource]int64](),
		RoofDistance: projection.RoofSupport, Plan: plan, Survey: survey,
		OutdoorCurve: projection.OutdoorCurve, FullRotRateC: projection.FullRotRateC,
	}
	if rooms, known := projection.Rooms.Value(); known {
		f.Dining = rooms.Dining
	}
	if ground, known := colonyGround(projection); known {
		f.Built = policy.BuiltRooms(plan, ground)
	}
	if prisoners, known := projection.Facts.Prisoners.Value(); known {
		f.Prisoners = domain.Known(heldPrisoners(prisoners))
	}
	if len(needs) > 0 {
		f.Stock = domain.Known(needs)
	}
	if ledger.Status == policy.LedgerViewReconciled {
		orders := make([]policy.OrderSpec, 0, len(ledger.Orders))
		for _, o := range ledger.Orders {
			orders = append(orders, policy.OrderSpec{Recipe: o.Recipe, Ingredients: o.Ingredients, Worker: o.Worker, Mode: domain.BillMode(o.Mode), Target: o.Target, BenchKind: o.BenchKind, Product: policy.Resource(o.Product)})
		}
		f.Orders = domain.Known(orders)
	}
	return f
}

// stationAdviceAttrs is the layout_stations row's attrs: each group with its
// stations, floor, predicted role, costs and the built rooms matched to it,
// the clustering's conflicts and the embedding's report.
func stationAdviceAttrs(a policy.StationAdvice) map[string]any {
	blocks := map[int]policy.Rectangle{}
	for _, b := range a.Embedding.Blocks {
		blocks[b.Group] = b.Interior
	}
	groups := make([]map[string]any, 0, len(a.Groups))
	for _, g := range a.Groups {
		stations := make([]map[string]any, 0, len(g.Group.Stations))
		for _, s := range g.Group.Stations {
			stations = append(stations, map[string]any{"node": s.Node, "count": s.Count})
		}
		requires := make([]string, 0, len(g.Group.Requires))
		for _, req := range g.Group.Requires {
			requires = append(requires, string(req))
		}
		costTicks := 0.0
		for _, c := range g.Group.Costs {
			costTicks += c.TicksPerDay
		}
		row := map[string]any{
			"group": g.Group.ID, "role": string(g.Group.Role), "stations": stations, "requires": requires,
			"footprint": g.Group.Footprint, "clearance": g.Group.Clearance, "area": g.Group.Area,
			"cost_ticks_per_day": costTicks, "lost_effects": len(g.Group.LostEffects),
			"rooms": g.Rooms, "built_area": g.Capacity, "outgrown": g.Outgrown,
		}
		if e := g.Exposure; e != nil {
			row["exposure"] = map[string]any{"satisfied": e.Satisfied, "vents": e.Vents, "peak_c": e.PeakC, "threshold_c": e.ThresholdC, "rot": "slowed"}
		}
		if b, ok := blocks[g.Group.ID]; ok {
			row["block"] = map[string]any{"x": b.X, "z": b.Z, "width": b.Width, "height": b.Height}
		}
		groups = append(groups, row)
	}
	var conflicts, issues []map[string]any
	for _, d := range a.Clustering.Decisions {
		if d.Conflict != "" {
			conflicts = append(conflicts, map[string]any{"a": d.A, "b": d.B, "conflict": d.Conflict})
		}
	}
	for _, d := range a.Embedding.Decisions {
		if d.Verdict != policy.EmbedPlace {
			issues = append(issues, map[string]any{"group": d.Group, "other": d.Other, "verdict": d.Verdict, "reason": d.Reason, "requirement": string(d.Requirement)})
		}
	}
	s := a.Embedding.Score
	return map[string]any{
		"nodes": len(a.Graph.Nodes), "edges": len(a.Graph.Edges), "groups": groups, "conflicts": conflicts,
		"embed": map[string]any{
			"level": a.Embedding.ObstacleLevel(), "placed": len(a.Embedding.Blocks), "issues": issues,
			"cells": s.Cells, "total": s.Total(), "soil": s.Soil, "edge": s.Edge, "centre": s.Centre, "walk": s.Walk, "unmet_links": s.UnmetLinks,
			"seed": fmt.Sprintf("%d,%d", a.Embedding.Seed.X, a.Embedding.Seed.Z),
		},
	}
}
