package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

// layoutReplanEvery is the fewest ticks between survey reads for any
// layout trigger (one game hour): a colony the grown plan still
// cannot house waits an hour instead of re-reading the map every review.
const layoutReplanEvery domain.Tick = 2500

// layoutTerrainCheckEvery is the terrain check: every game hour a review
// re-reads the survey and replans the layout when it changed.
const layoutTerrainCheckEvery domain.Tick = 2500

// layoutInputs is what an incremental replan reads; a survey whose inputs
// match the last replan's (of the same saved plan) replans nothing.
type layoutInputs struct {
	planTick domain.Tick
	key      string
	bounds   policy.Bounds
	cells    []policy.SurveyCell
}

func (a layoutInputs) same(b layoutInputs) bool {
	return a.planTick == b.planTick && a.key == b.key && a.bounds == b.bounds && slices.Equal(a.cells, b.cells)
}

// grownFor is the tier and sorted finished research a plan is grown for.
func grownFor(projection observation.ColonyProjection) string {
	finished, _ := observation.FinishedResearch(projection.Facts.Research).Value()
	research := make([]string, 0, len(finished))
	for _, id := range finished {
		research = append(research, string(id))
	}
	slices.Sort(research)
	return fmt.Sprint(layoutTier(projection), "|", strings.Join(research, ","))
}

// layoutSurvey is the whole-map survey behind the layout plan, read off the
// projection's planning window and the load's definition catalog. A
// reviewer whose native serves no definitions, or a projection without a
// window, plans nothing. The catalog comes back with it so one review reads
// the definitions once (the station chain reuses it).
func (r *Rounder) layoutSurvey(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection) (policy.MapSurvey, *bridge.DefinitionCatalog, error) {
	source, ok := r.native.(observation.DefinitionSource)
	if !ok || projection.Region == (policy.Rectangle{}) {
		return policy.MapSurvey{}, nil, errors.New("layout survey: no definitions or planning window")
	}
	catalog, err := source.DefinitionCatalog(ctx, controlIdentity(snapshot))
	if err != nil {
		return policy.MapSurvey{}, nil, err
	}
	survey, err := bridge.SurveyFromCells(projection.Cells, projection.Bounds, catalog)
	return survey, catalog, err
}

// reviewLayoutPlan serves the saved v2 layout plan on the
// projection. It derives one at once when none is saved (then at most
// once an hour) and re-reads the survey every hour, replanning when a pawn,
// tier, research, need or the terrain changed since the last replan. The
// replan is the station chain's tiled plan (policy.ReplanLayout): the chain
// keeps the rooms that stand, the rooms a journal plan works on or a pawn
// sleeps in and the rooms it has a node for, and sites the rest again.
func (r *Rounder) reviewLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	tick := projection.Identity.Tick
	layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	pawns, _ := projection.Facts.Colonists.Value()
	// The last check is the newer of the saved plan's tick and the last
	// survey this process read; a rewind past it or a restart falls back
	// to the plan's tick.
	checked := layout.Tick
	if r.planChecked > checked && r.planChecked <= tick {
		checked = r.planChecked
	}
	hourly := !r.planSurveyed || tick-checked >= layoutReplanEvery
	missing := !haveLayout && hourly
	// Every hour a review re-reads the survey and replans when its inputs
	// (the chain's facts, the survey) changed: a need, a pawn or a room that
	// stands is among them, so no trigger of its own is needed.
	terrain := haveLayout && tick-checked >= layoutTerrainCheckEvery
	// New research or a new tier: other research unlocks rooms too.
	grown := grownFor(*projection)
	research := haveLayout && grown != r.planGrownFor && hourly
	if missing || terrain || research {
		replanned := false
		if survey, catalog, err := r.layoutSurvey(ctx, snapshot, *projection); err != nil {
			_ = err // a failed survey retries on the next review
		} else {
			r.planChecked, r.planSurveyed = tick, true
			// A fresh plan latches the map's climate; unknown reads warm.
			survey.Cold, _ = projection.ColdMap.Value()
			survey.Hot, _ = projection.HotMap.Value()
			topology, _ := projection.PowerPlanning.Value()
			// The barn follows the herd plan's target herd.
			animals := projection.Facts.PenAnimals()
			if !haveLayout {
				err = r.deriveLayoutPlan(ctx, snapshot, tick, *projection, survey, catalog, topology.Geysers, animals, perimeterWall(*projection))
				r.planGrownFor, r.planPawns, r.planInputs = grown, int(pawns), layoutInputs{}
			} else {
				var growth policy.RoomGrowth
				if growth, err = r.roomGrowth(ctx, snapshot, *projection, layout.Plan, survey, r.stockpiles.layoutDemand(stockpileWorld(snapshot))); err != nil {
					return err
				}
				// Every new building moves the fixed rooms, which are the key.
				keyed := growth
				keyed.Grazing = nil
				r.stationAdvice(ctx, snapshot, *projection, layout.Plan, policy.TilePinned(layout.Plan, growth), growth, growth.Demand, survey, catalog)
				inputs := layoutInputs{planTick: layout.Tick, key: fmt.Sprint(grown, pawns, topology.Geysers, keyed, animals, r.stationInputs.key), bounds: survey.Bounds, cells: survey.Cells}
				if !inputs.same(r.planInputs) {
					reason := layoutReasons(map[string]bool{"terrain": inputs.bounds != r.planInputs.bounds || !slices.Equal(inputs.cells, r.planInputs.cells), "pawns": int(pawns) != r.planPawns, "research": research})
					if replanned, err = r.replanLayout(ctx, snapshot, tick, layout.Plan, survey, growth, animals, int(pawns), reason, topology.Geysers, perimeterWall(*projection)); err == nil && replanned {
						r.planGrownFor, r.planPawns, r.planInputs = grown, int(pawns), inputs
					}
				}
			}
			if err != nil {
				return err
			}
			if layout, haveLayout, err = r.layoutPlan(ctx, snapshot, tick); err != nil {
				return err
			}
			if replanned && haveLayout {
				// The recorded replan is the plan the same inputs plan next.
				r.planInputs.planTick = layout.Tick
			}
		}
	}
	if haveLayout {
		projection.LayoutPlan = domain.Known(layout.Plan)
	}
	r.drawLayoutOverlay(ctx, snapshot, projection, layout, haveLayout)
	return nil
}

// roomGrowth is what the review holds of the colony for the replan beyond the
// chain's facts: the throne room the title asks, the shelter's retirement, the
// rooms with something of ours on them (read here, with the journal's open
// plans, only once a replan is due), the rooms a journal plan works on, the
// rooms of an ended role in use, the bedrooms a pawn still sleeps in, the herd
// and the ring's hold. Fixed stays nil while the census or the plan catalog is
// unknown, which pins every room.
func (r *Rounder) roomGrowth(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, plan policy.LayoutPlan, survey policy.MapSurvey, demand policy.RoomDemand) (policy.RoomGrowth, error) {
	growth := policy.RoomGrowth{HerdUnits: projection.Facts.HerdUnits(), Pens: projection.Facts.PenWants(), Demand: demand}
	need, owed := throneNeed(projection)
	growth.ThroneArea = policy.ThroneAreaOwed(plan, need, owed)
	if pasture, known := projection.Facts.Pasture.Value(); known {
		growth.Grazing = policy.GrazingGround(survey, pasture)
	}
	// A room whose need has ended and that is in use stays.
	if construction, ck := projection.Facts.CurrentConstruction.Value(); ck && construction.Colony {
		ended := policy.EndedRoomRoles(projection.WorkPawns, projection.Facts.Ideology, projection.Facts.Containment, projection.Isolation, childRoomNeeds(projection))
		if policy.RoomsOfRoles(plan, ended) > 0 {
			growth.InUse = policy.RoomsInUse(plan, construction.Buildings, furnitureDefinitions(projection))
		}
	}
	// Bedrooms, workshop and laboratory stand and every colonist is housed:
	// the shelter leaves the plan. Re-read every review, no latch.
	census, rk := projection.Rooms.Value()
	sleeping, sk := projection.Facts.Sleeping.Value()
	construction, ck := projection.Facts.CurrentConstruction.Value()
	growth.RetireShelter = rk && sk && ck && construction.Colony && policy.ShelterRetirable(plan, census, sleeping, construction.Buildings)
	if occupied, origins, ok := r.layoutOccupied(ctx, projection, plan); ok {
		ground, _ := colonyGround(projection)
		growth.Fixed = policy.FixedRooms(plan, ground, occupied)
		growth.InFlight = policy.InFlightRooms(plan, origins)
	} else {
		// Open plans unknown: no shelter is dropped over work in flight.
		growth.RetireShelter = false
	}
	growth.Slept = policy.SleptBedrooms(plan, projection.Rooms, projection.Facts.Sleeping)
	// The ring is laid around what the defense record holds of it.
	var err error
	growth.Perimeter, err = r.perimeterHold(ctx, snapshot)
	return growth, err
}

// overlayRedrawEvery is how often an unchanged plan's overlay is redrawn
// (one game day).
const overlayRedrawEvery domain.Tick = 60000

// overlayLayer is the native overlay layer the layout plan draws on;
// fieldLayer holds its field zones, hidden natively by default.
const (
	overlayLayer = "layout"
	fieldLayer   = "fields"
)

// LayoutOverlayNative draws the layout plan as a native overlay layer
// (bridge.Client.DrawOverlay).
type LayoutOverlayNative interface {
	DrawOverlay(context.Context, *c.Identity, string, policy.LayoutOverlay, bool) (*p.OverlayApplied, bridge.Result, error)
}

// drawLayoutOverlay rewrites the overlay when the plan changed or a day
// passed since the last draw; with the overlay off it deletes the owned
// plans once per process. Output only: a failure is logged, never fatal.
func (r *Rounder) drawLayoutOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	r.drawHeatOverlay(ctx, native, snapshot, projection)
	tick := projection.Identity.Tick
	if !r.layoutOverlay || !haveLayout {
		if !r.overlayCleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, policy.LayoutOverlay{}, false); err != nil {
				return
			}
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), fieldLayer, policy.LayoutOverlay{}, false); err != nil {
				return
			}
			r.overlayCleared = true
		}
		return
	}
	// The defense tier nodes are cut from the defense round's census, not
	// stored with the plan: they draw beside it and move with each cut.
	drawn := layout.Plan
	key := fmt.Sprint("v2@", layout.Tick)
	if tiers := r.tiers.Load(); tiers != nil {
		drawn = drawn.WithDefenseTiers(tiers.nodes)
		key = fmt.Sprint(key, "/tiers@", tiers.key)
	}
	if key == r.overlayKey && tick >= r.overlayDrawn && tick-r.overlayDrawn < overlayRedrawEvery {
		return
	}
	overlay := drawn.Overlay(projection.Bounds)
	if r.stationHeld {
		layout.Plan.RequirementOverlay(r.stationLast, projection.Bounds, &overlay)
	}
	layer, fields := policy.SplitFields(overlay)
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), fieldLayer, fields, len(fields.Layers) > 0); err != nil {
		return
	}
	_, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, layer, true)
	if err != nil {
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
}

// perimeterHold reads what the defense record holds of the plan's ring nodes
// (none while it has no record).
func (r *Rounder) perimeterHold(ctx context.Context, snapshot domain.GenerationSnapshot) (policy.PerimeterHold, error) {
	record, ok, err := r.player.journal.LoadDefenseLayout(ctx, store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map})
	if err != nil || !ok {
		return policy.PerimeterHold{}, err
	}
	return record.PerimeterHold(), nil
}

// layoutPlan reads the v2 layout plan. A saved plan that no longer
// decodes or validates reads as none, logged once per process, so the next
// survey derives a fresh one.
func (r *Rounder) layoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (store.LayoutPlanRecord, bool, error) {
	record, ok, err := r.player.journal.LayoutPlan(ctx, snapshot, tick)
	if err == nil && record.Invalid && !r.layoutInvalidLogged {
		r.layoutInvalidLogged = true
	}
	return record, ok, err
}

// deriveLayoutPlan lays a fresh v2 plan over survey and the reported
// geysers from the station chain and records it. A fact the colony has not read
// leaves the plan at the seed rooms (the layout_stations row says which); a
// chain that fails records nothing and the next hourly review derives again.
func (r *Rounder) deriveLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, projection observation.ColonyProjection, survey policy.MapSurvey, catalog *bridge.DefinitionCatalog, geysers []policy.PowerGeyser, animals int, wall policy.PerimeterWall) error {
	pawns, _ := projection.Facts.Colonists.Value()
	colonists := int(pawns)
	start := time.Now()
	f := FirstPlanFacts(projection, survey, r.demand.get(snapshot).Needs, r.WorkLedgerView())
	f.Crops, f.FoodReserveDays = fieldCrops(projection), r.seasonal(projection.Facts).FoodTargetDays
	f, err := stationFacts(catalog, f)
	var fact domain.Fact[policy.LayoutPlan]
	var advice policy.StationAdvice
	if err == nil {
		fact, advice, err = policy.DeriveLayoutPlan(survey, f, geysers, animals)
	}
	row, _, _ := stationOutcome(advice, err)
	row.Dur = time.Since(start)
	row.Attrs["derive"] = true
	telemetry.Decide(ctx, row)
	logInterior(ctx, advice)
	switch {
	case errors.Is(err, policy.ErrStationFacts):
		telemetry.Decide(ctx, layoutPlanDecision("skipped", "facts_unknown", colonists, "", map[string]any{"fact": err.Error()}))
		return nil
	case err != nil:
		telemetry.Decide(ctx, layoutPlanDecision("failed", "chain_error", colonists, "", map[string]any{"error": err.Error()}))
		return nil
	}
	plan, known := fact.Value()
	if !known {
		telemetry.Decide(ctx, layoutPlanDecision("skipped", "no_core", colonists, "", nil))
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, plan); err != nil {
		return err
	}
	telemetry.Decide(ctx, layoutPlanDecision("planned", "", colonists, plan.Summary(), nil))
	logPerimeter(ctx, plan, colonists)
	logPerimeterCut(ctx, plan, survey, wall, colonists)
	return nil
}

// logInterior writes the layout_interior row of each interior search move the
// tiler kept.
func logInterior(ctx context.Context, advice policy.StationAdvice) {
	if advice.Tiled == nil {
		return
	}
	for _, d := range advice.Tiled.Decisions {
		if d.Reason == policy.ReasonInteriorSearchMove {
			telemetry.Decide(ctx, telemetry.Decision{Kind: "layout_interior", Component: "layout", Verdict: d.Verdict, Reason: d.Reason, Target: "interior", Attrs: map[string]any{"group": d.Group, "move": d.Move}})
		}
	}
}

// logPerimeter writes the layout_plan rows of the traced ring and its opening
// (target perimeter): the ring is traced, the opening stands where the raider
// routes cross it or is absent.
func logPerimeter(ctx context.Context, plan policy.LayoutPlan, colonists int) {
	for _, d := range policy.PerimeterDecisions(plan) {
		dec := layoutPlanDecision(d.Verdict, d.Reason, colonists, "", map[string]any{"node": d.Node})
		dec.Target = "perimeter"
		telemetry.Decide(ctx, dec)
	}
}

// replanLayout replans the recorded v2 plan from the station chain's tiled plan
// over a fresh survey (policy.ReplanLayout) and records it when it changed. A
// chain or tiler that cannot plan keeps the plan the colony has: the layout_replan_tiled
// row says why, and the replan is reported undone (false) so the next trigger
// tries again. It reports whether the plan was replanned, changed or not.
func (r *Rounder) replanLayout(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan, survey policy.MapSurvey, growth policy.RoomGrowth, animals, pawns int, reason string, geysers []policy.PowerGeyser, wall policy.PerimeterWall) (bool, error) {
	decision := telemetry.Decision{Kind: "layout_replan_tiled", Component: "layout", Target: "plan"}
	if !r.stationHeld {
		decision.Verdict, decision.Reason = "skipped", "station_advice_unavailable"
		telemetry.Decide(ctx, decision)
		return false, nil
	}
	facts := r.stationFacts
	start := time.Now()
	next, advice, err := policy.ReplanLayout(plan, facts, survey, geysers, growth, animals)
	decision.Dur = time.Since(start)
	switch {
	case errors.Is(err, policy.ErrStationFacts):
		decision.Verdict, decision.Reason, decision.Attrs = "skipped", "facts_unknown", map[string]any{"fact": err.Error()}
		telemetry.Decide(ctx, decision)
		return false, nil
	case err != nil:
		decision.Level, decision.Verdict, decision.Reason, decision.Attrs = slog.LevelWarn, "failed", "tile_error", map[string]any{"error": err}
		telemetry.Decide(ctx, decision)
		return false, nil
	}
	changed := !policy.SamePlan(plan, next)
	decision.Verdict, decision.Reason = "replanned", "tiled_plan"
	decision.Attrs = tiledReplanAttrs(policy.DiffReplanTile(plan, facts.Built, advice), len(facts.Built), len(advice.Tiled.Plan.AllRooms()))
	decision.Attrs["changed"] = changed
	telemetry.Decide(ctx, decision)
	logInterior(ctx, advice)
	if !changed {
		return true, nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, next); err != nil {
		return false, err
	}
	telemetry.Decide(ctx, layoutPlanDecision("replanned", reason, pawns, next.Summary(), nil))
	if policy.PerimeterChanged(plan, next) {
		logPerimeter(ctx, next, pawns)
		logPerimeterCut(ctx, next, survey, wall, pawns)
	}
	return true, nil
}

// layoutPlanDecision is the layout_plan row of a plan derived (planned),
// grown (replanned, reason the triggers that fired) or not derivable
// (skipped), with the colonist count and the plan summary as attrs.
func layoutPlanDecision(verdict, reason string, colonists int, summary string, extra map[string]any) telemetry.Decision {
	attrs := map[string]any{"colonists": colonists, "summary": summary}
	for k, v := range extra {
		attrs[k] = v
	}
	return telemetry.Decision{Kind: "layout_plan", Component: "layout", Verdict: verdict, Reason: reason, Target: "plan", Attrs: attrs}
}

// layoutReasons names the triggers that fired, sorted, for the
// layout_plan replanned row.
func layoutReasons(fired map[string]bool) string {
	var out []string
	for name, on := range fired {
		if on {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return strings.Join(out, ",")
}

// heatRedrawEvery is how often the traffic heat layers are redrawn (one
// game hour).
const heatRedrawEvery domain.Tick = 2500

// drawHeatOverlay redraws a "heat.<layer>" overlay layer per traffic layer
// from the census's busiest cells, on its own hourly cadence; with
// the overlay off it removes them once. Output only, like the layout.
func (r *Rounder) drawHeatOverlay(ctx context.Context, native LayoutOverlayNative, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) {
	census, known := projection.Facts.Upkeep.Flooring.Value()
	on := r.layoutOverlay && known
	if !on && r.heatCleared {
		return
	}
	tick := projection.Identity.Tick
	if on && r.heatDrawn != 0 && tick >= r.heatDrawn && tick-r.heatDrawn < heatRedrawEvery {
		return
	}
	for _, layer := range policy.TrafficLayers {
		var heat policy.LayoutOverlay
		if on {
			heat = policy.TrafficOverlay(census.Traffic, layer, projection.Bounds)
		}
		if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), "heat."+string(layer), heat, on && len(heat.Layers) > 0); err != nil {
			return
		}
	}
	r.heatDrawn, r.heatCleared = tick, !on
}

// layoutTier is the tech tier new bedroom wings are sized for; an
// unknown tier reads Camp.
func layoutTier(projection observation.ColonyProjection) policy.TechTier {
	tier, _ := projection.TechTier.Value()
	return tier
}
