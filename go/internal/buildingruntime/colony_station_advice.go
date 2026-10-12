package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
func (r *Rounder) stationAdvice(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, plan policy.LayoutPlan, pinned map[int32]bool, survey policy.MapSurvey, catalog *bridge.DefinitionCatalog) (policy.StationAdvice, bool) {
	f := stationFactsOf(projection, plan, survey, r.demand.get(snapshot).Needs, r.WorkLedgerView())
	f.Pinned = pinned
	f.Crops, f.FoodReserveDays = fieldCrops(projection), r.seasonal(projection.Facts).FoodTargetDays
	f.Suites = suiteWishes(projection, plan, r.stage)
	inputs := layoutInputs{key: fmt.Sprint(snapshot.Load, "|", f.InputsKey()), bounds: survey.Bounds, cells: survey.Cells}
	if inputs.same(r.stationInputs) {
		return r.stationLast, r.stationHeld
	}
	start := time.Now()
	facts, err := stationFacts(catalog, f)
	decision, advice, ok := stationDecision(facts, err)
	decision.Dur = time.Since(start)
	telemetry.Decide(ctx, decision)
	for _, d := range advice.Regroup {
		telemetry.Decide(ctx, regroupDecision(d))
	}
	r.stationInputs, r.stationLast, r.stationHeld = inputs, advice, ok
	return advice, ok
}

// regroupDecision is the layout_regroup row of one regrouping decision: a move
// applied (kind merge, split or move, the pair of nodes it rules and the total
// before and after, in score points) or the reason none was.
func regroupDecision(d policy.RegroupDecision) telemetry.Decision {
	return telemetry.Decision{Kind: "layout_regroup", Component: "layout", Verdict: d.Verdict, Reason: d.Reason, Target: "groups",
		Attrs: map[string]any{"move": d.Kind, "a": d.A, "b": d.B, "before": d.Before, "after": d.After, "walk": d.Walk, "poison": d.Poison, "clean": d.Clean, "lost_effects": d.Lost}}
}

// stationDecision runs the chain over facts (when they were read, err nil) and
// is the layout_stations row of the outcome: advised, skipped for an unknown
// fact, or failed. The advice is returned when it was made.
func stationDecision(facts policy.StationFacts, err error) (telemetry.Decision, policy.StationAdvice, bool) {
	var advice policy.StationAdvice
	if err == nil {
		advice, err = policy.AdviseStations(facts)
	}
	return stationOutcome(advice, err)
}

// stationOutcome is the layout_stations row of advice made (or err, the chain's).
func stationOutcome(advice policy.StationAdvice, err error) (telemetry.Decision, policy.StationAdvice, bool) {
	decision := telemetry.Decision{Kind: "layout_stations", Component: "layout", Target: "plan"}
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
func stationFacts(catalog *bridge.DefinitionCatalog, f policy.StationFacts) (policy.StationFacts, error) {
	var err error
	if f.Recipes, err = catalog.StationRecipeFlows(f.Catalog); err != nil {
		return policy.StationFacts{}, err
	}
	stock, _ := f.Stock.Value()
	f.StackLimit, f.Perishable, err = catalog.StationItemFacts(stock, f.Recipes)
	return f, err
}

// growingCrops are the crops the colony's growing zones are sown with, each
// once, in plant-definition order: the field nodes of the station graph follow
// the crops the field planner has chosen (cropChoices is its read), never a
// choice of the chain's.
func growingCrops(projection observation.ColonyProjection) []policy.CropChoice {
	sown := map[string]bool{}
	for _, farm := range projection.Farms {
		if farm.Crop != "" {
			sown[farm.Crop] = true
		}
	}
	var out []policy.CropChoice
	for _, choice := range cropChoices(projection) {
		if sown[choice.Name] {
			out = append(out, choice)
		}
	}
	return out
}

// fieldCrops are the crops the plan's fields are sized for (#2961): the sown
// crops, and while none is sown the first of the default table the catalog
// offers (policy.DefaultFieldCrop, reason ReasonFieldDefaultCrop), because a
// field node exists only for a crop and a colony that sows nothing would never
// get a field to sow.
func fieldCrops(projection observation.ColonyProjection) []policy.CropChoice {
	if sown := growingCrops(projection); len(sown) > 0 {
		return sown
	}
	if crop, ok := policy.DefaultFieldCrop(cropChoices(projection)); ok {
		return []policy.CropChoice{crop}
	}
	return nil
}

// fieldBlocks are the interiors of the fields the advice placed.
func fieldBlocks(a policy.StationAdvice) []policy.Rectangle {
	out := make([]policy.Rectangle, 0, len(a.Embedding.Fields))
	for _, f := range a.Embedding.Fields {
		out = append(out, f.Interior)
	}
	return out
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
		VentAvailable: projection.DefinitionAvailable(policy.ClimateDefs().VentDef),
		ExpectedMood:  expectedMood(projection.Facts.MoodPawns),
	}
	if mismatches, err := roleMismatches(projection); err == nil { // an unread or inconsistent census leaves the guard off
		f.Mismatches = mismatches
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

// FirstPlanFacts are the station facts of a first plan over survey
// (policy.DeriveLayoutPlan): every fact the projection, the resource demand
// needs and the work ledger hold, nothing standing, the throne room a title asks
// (ThroneArea) and what the colony has not read left unknown, which a first plan
// grows from the seed rooms (StationFacts.Derive). The catalog rows are added
// by the caller (stationFacts).
func FirstPlanFacts(projection observation.ColonyProjection, survey policy.MapSurvey, needs map[policy.Resource]int64, ledger policy.LedgerView) policy.StationFacts {
	f := stationFactsOf(projection, policy.LayoutPlan{}, survey, needs, ledger)
	f.Derive = true
	need, owed := throneNeed(projection)
	f.ThroneArea = domain.Known(policy.ThroneAreaOwed(policy.LayoutPlan{}, need, owed))
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
		if c := g.Climate; c != nil {
			options := make([]map[string]any, 0, len(c.Options))
			for _, o := range c.Options {
				options = append(options, map[string]any{"opening": string(o.Opening), "mean_w": o.Watts.MeanW, "peak_w": o.Watts.PeakW, "heating_w": o.Watts.HeatingW, "cooling_w": o.Watts.CoolingW, "build_w": o.BuildW, "total_w": o.TotalW})
			}
			row["climate"] = map[string]any{"cells": c.Cells, "max_c": c.Band.MaxC, "best": string(c.Best), "options": options}
		}
		if b, ok := blocks[g.Group.ID]; ok {
			row["block"] = map[string]any{"x": b.X, "z": b.Z, "width": b.Width, "height": b.Height}
		}
		groups = append(groups, row)
	}
	var conflicts, issues, built []map[string]any
	for _, d := range a.Clustering.Decisions {
		if d.Conflict != "" {
			conflicts = append(conflicts, map[string]any{"a": d.A, "b": d.B, "conflict": d.Conflict})
		}
	}
	for _, d := range a.Embedding.Decisions {
		if d.Verdict == policy.EmbedKeep || d.Verdict == policy.EmbedMove {
			built = append(built, map[string]any{"group": d.Group, "room": d.Room, "verdict": d.Verdict, "reason": d.Reason})
		} else if d.Verdict != policy.EmbedPlace {
			issues = append(issues, map[string]any{"group": d.Group, "other": d.Other, "verdict": d.Verdict, "reason": d.Reason, "requirement": string(d.Requirement), "field": d.Field})
		}
	}
	var sites []map[string]any
	for _, p := range a.Sites.Sites {
		row := map[string]any{"node": p.Node, "x": p.Origin.X, "z": p.Origin.Z, "anchored": p.Anchored}
		for _, l := range p.Lanes {
			if l.Wind {
				row["rotation"], row["blocked"], row["output"] = string(p.Rotation), l.Blocked, l.WindOutput()
			}
		}
		sites = append(sites, row)
	}
	var siteIssues []map[string]any
	for _, d := range a.Sites.Decisions {
		if d.Verdict != policy.EmbedPlace {
			siteIssues = append(siteIssues, map[string]any{"node": d.Node, "verdict": d.Verdict, "reason": d.Reason})
		}
	}
	fields := make([]map[string]any, 0, len(a.Embedding.Fields))
	for _, f := range a.Embedding.Fields {
		fields = append(fields, map[string]any{"field": f.ID, "x": f.Interior.X, "z": f.Interior.Z, "width": f.Interior.Width, "height": f.Interior.Height, "reason": policy.ReasonEmbedFieldPlaced})
	}
	// The tiled plan's vents and why a room has none (#2915).
	var vents, ventIssues []map[string]any
	if a.Tiled != nil {
		for _, v := range a.Tiled.Plan.LiveSites(policy.NodeVent) {
			vents = append(vents, map[string]any{"x": v.Origin.X, "z": v.Origin.Z, "rotation": string(v.Rotation), "reason": v.Reason})
		}
		for _, d := range a.Tiled.Decisions {
			if strings.HasPrefix(d.Reason, "tile_vent") && d.Verdict != policy.TilePlace {
				ventIssues = append(ventIssues, map[string]any{"group": d.Group, "verdict": d.Verdict, "reason": d.Reason})
			}
		}
	}
	comfort := make([]map[string]any, 0, len(a.Comfort))
	for _, c := range a.Comfort {
		comfort = append(comfort, map[string]any{"node": c.Node, "def": c.Option.Def, "stuff": c.Option.Stuff, "comfort": c.Option.Comfort, "priced": c.Priced, "gain": c.Gain, "benefit_w": c.BenefitW, "cost_w": c.CostW})
	}
	s := a.Embedding.Score
	return map[string]any{
		"nodes": len(a.Graph.Nodes), "edges": len(a.Graph.Edges), "groups": groups, "conflicts": conflicts, "seed_only": a.SeedOnly,
		"sites": sites, "site_issues": siteIssues, "vents": vents, "vent_issues": ventIssues, "comfort": comfort,
		"embed": map[string]any{
			"level": a.Embedding.ObstacleLevel(), "placed": len(a.Embedding.Blocks), "issues": issues, "fields": fields,
			"cells": s.Cells, "total": s.Total(), "soil": s.Soil, "edge": s.Edge, "centre": s.Centre, "walk": s.Walk, "unmet_links": s.UnmetLinks, "switch": s.Switch, "built": built,
			"seed": fmt.Sprintf("%d,%d", a.Embedding.Seed.X, a.Embedding.Seed.Z),
		},
	}
}

// expectedMood is the colonists' mean mood level, each colonist at the level
// its thoughts forecast (Target) or else its current one: the mood price's
// baseline (#2975). Unknown while any colonist has neither, so the comfort tiers
// go unpriced rather than priced on part of the colony.
func expectedMood(pawns domain.Fact[[]policy.MoodPawn]) domain.Fact[float64] {
	rows, ok := pawns.Value()
	if !ok || len(rows) == 0 {
		return domain.Unknown[float64]()
	}
	sum := 0.0
	for _, p := range rows {
		level, known := p.Target.Value()
		if !known {
			level, known = p.Mood.Value()
		}
		if !known {
			return domain.Unknown[float64]()
		}
		sum += level
	}
	return domain.Known(sum / float64(len(rows)))
}
