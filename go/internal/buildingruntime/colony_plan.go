package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"strings"

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
// layout trigger (one game hour, #1290): a colony the grown plan still
// cannot house waits an hour instead of re-reading the map every review.
const layoutReplanEvery domain.Tick = 2500

// layoutTerrainCheckEvery is the terrain check: every game hour a review
// re-reads the survey and replans the layout when it changed (#1290).
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

// MapSurveyNative is the optional native whole-map read behind the layout
// plan (bridge.Client.ReadMapSurvey). A reviewer whose native lacks it, or
// refuses the foundation field, plans nothing.
type MapSurveyNative interface {
	ReadMapSurvey(context.Context, *c.Identity, policy.Bounds) (policy.MapSurvey, bridge.Result, error)
}

// reviewLayoutPlan serves the saved v2 layout plan (#783) on the
// projection. It derives one at once when none is saved (then at most
// once an hour) and re-reads the survey every hour (#1290), growing the
// plan when a pawn, tier, research, need or the terrain changed since the
// last replan. The replan is incremental: built rooms never move.
func (r *Rounder) reviewLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	tick := projection.Identity.Tick
	layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	pawns, known := projection.Facts.Colonists.Value()
	// The last check is the newer of the saved plan's tick and the last
	// survey this process read; a rewind past it or a restart falls back
	// to the plan's tick.
	checked := layout.Tick
	if r.planChecked > checked && r.planChecked <= tick {
		checked = r.planChecked
	}
	outgrown := known && haveLayout && layout.Plan.LayoutOutgrown(int(pawns)) && tick-layout.Tick >= layoutReplanEvery
	hourly := !r.planSurveyed || tick-checked >= layoutReplanEvery
	missing := !haveLayout && hourly
	terrain := haveLayout && tick-checked >= layoutTerrainCheckEvery
	// The pens, barn and vet room follow the herd plan's target herd (#1633).
	animals := projection.Facts.PenAnimals()
	// New research or a new tier (#1290): other research unlocks rooms too.
	grown := grownFor(*projection)
	research := haveLayout && grown != r.planGrownFor && hourly
	// Every tomb full (#857): grow one more, at most once an hour.
	tombs := 0
	tomb := haveLayout && tombsFull(layout.Plan, *projection)
	if tomb {
		tombs = layout.Plan.TombRooms() + 1
	}
	tomb = tomb && hourly
	// A pawn owed a suite no planned suite answers (#1216) is planned a new
	// suite block, sized at siting (#1951); suites already planned never
	// change. No trigger of its own: the hourly terrain check re-reads the
	// survey and the suites are in the replan's inputs key, so a new claim
	// is planned within the hour (#1958).
	var suites []float64
	if haveLayout {
		var claims []policy.SuiteClaim
		suites, claims = suiteTargets(*projection, layout.Plan, r.stage)
		r.logSuiteClaims(ctx, claims)
	}
	suite := haveLayout && len(suites) > layout.Plan.SuiteRooms()
	// A colonist who holds or can claim a title that asks for a throne room
	// the plan lacks (#1601): grow one sized to the title's area.
	var growth policy.RoomGrowth
	growth.HerdUnits = projection.Facts.HerdUnits()
	if need, owed := throneNeed(*projection); haveLayout {
		growth.ThroneArea = policy.ThroneAreaOwed(layout.Plan, need, owed)
		if owed {
			growth.ThroneMin = need.MinArea
		}
	}
	demand := r.stockpiles.layoutDemand(stockpileWorld(snapshot))
	throne := growth.ThroneArea > 0 && hourly
	// A baby, toddler or child owed a nursery, playroom or classroom the
	// plan lacks (#1680): grow it, sized to its furniture.
	if haveLayout {
		needs, defs := childRoomNeeds(*projection), furnitureDefinitions(*projection)
		growth.Child = policy.ChildRoomsOwed(layout.Plan, needs, defs)
		if ground, known := colonyGround(*projection); known && (policy.DuplicateRooms(layout.Plan) > 0 || policy.SurplusRoomsPossible(layout.Plan, growth.ThroneMin, demand)) {
			growth.Shapes, growth.Built = policy.ChildRoomShapes(needs, defs), policy.BuiltRooms(layout.Plan, ground)
		}
	}
	// An unbuilt room whose need has ended (#1824) leaves the plan.
	if haveLayout {
		construction, ck := projection.Facts.CurrentConstruction.Value()
		if ck && construction.Colony {
			ended := policy.EndedRoomRoles(projection.WorkPawns, projection.Facts.Ideology, projection.Facts.Containment, projection.Isolation, childRoomNeeds(*projection))
			if policy.RoomsOfRoles(layout.Plan, ended) > 0 {
				growth.Ended = ended
				growth.InUse = policy.RoomsInUse(layout.Plan, construction.Buildings, furnitureDefinitions(*projection))
			}
		}
	}
	children := (len(growth.Child) > 0 || growth.Built != nil || growth.InUse != nil) && hourly
	// Bedrooms, workshop and laboratory stand and every colonist is housed:
	// the shelter leaves the plan (#2046). Re-read every review, no latch.
	if haveLayout {
		census, rk := projection.Rooms.Value()
		sleeping, sk := projection.Facts.Sleeping.Value()
		construction, ck := projection.Facts.CurrentConstruction.Value()
		growth.RetireShelter = rk && sk && ck && construction.Colony && policy.ShelterRetirable(layout.Plan, census, sleeping)
	}
	retireShelter := growth.RetireShelter && hourly
	// Stored gear outgrew its zone (#1773): the storage planner's demand adds
	// the armory or wardrobe the plan lacks, at most once an hour.
	if haveLayout && (len(policy.GearRoomsOwed(layout.Plan, demand)) > 0 || policy.StorageRoomsOwed(layout.Plan, demand) > 0 || policy.YardRoomsOwed(layout.Plan, demand) > 0 || policy.GraveyardsOwed(layout.Plan, demand) > 0 || policy.SurplusRoomsPossible(layout.Plan, growth.ThroneMin, demand)) {
		growth.Demand = demand
	}
	gear := growth.Demand != (policy.RoomDemand{}) && hourly
	// A need for a room the plan starts without (hospital, lab, rec, butchery,
	// prison, battery) plans it, at most once an hour.
	if haveLayout {
		growth.Core = policy.CoreRoomsOwed(layout.Plan, coreRoomsWanted(*projection))
	}
	core := len(growth.Core) > 0 && hourly
	// The outskirts cluster holds the tomb, morgue, waste yard and incinerator from the start (#2185, #2187); a
	// plan that predates it is grown one, at most once an hour.
	if haveLayout && policy.OutskirtsOwed(layout.Plan) {
		growth.Outskirts = policy.OutskirtsSize()
	}
	outskirts := growth.Outskirts != [2]int32{} && hourly
	// The materials yard is planned from the start (#2192); a plan that predates
	// it, or a full yard (RoomDemand.Yard), is grown one.
	yard := haveLayout && policy.YardRoomsOwed(layout.Plan, demand) > 0 && hourly
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || missing || terrain || research || tomb || throne || children || retireShelter || gear || core || outskirts || yard) {
		replanned := false
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			_ = err // a failed survey retries on the next review
		} else {
			r.planChecked, r.planSurveyed = tick, true
			// A fresh plan latches the map's climate (#2044); unknown reads warm.
			survey.Cold, _ = projection.ColdMap.Value()
			survey.Hot, _ = projection.HotMap.Value()
			topology, _ := projection.PowerPlanning.Value()
			if !haveLayout {
				err = r.deriveLayoutPlan(ctx, snapshot, tick, survey, int(pawns), layoutTier(*projection), topology.Geysers, animals)
				r.planGrownFor, r.planPawns, r.planInputs = grown, int(pawns), layoutInputs{}
			} else {
				// Fixed rooms are what the replan keeps (#1958): read here, with
				// the journal's open plans, only once a replan is due. Nil while
				// the census or the plan catalog is unknown keeps every room.
				if occupied, origins, ok := r.layoutOccupied(ctx, *projection, layout.Plan); ok {
					ground, _ := colonyGround(*projection)
					growth.Fixed, growth.Occupied = policy.FixedRooms(layout.Plan, ground, occupied), occupied
					growth.InFlight = policy.InFlightRooms(layout.Plan, origins)
				} else {
					// Open plans unknown: no shelter is dropped over work in flight.
					growth.RetireShelter = false
				}
				// Every new building moves Occupied; the fixed rooms are its key.
				keyed := growth
				keyed.Occupied = nil
				inputs := layoutInputs{planTick: layout.Tick, key: fmt.Sprint(grown, pawns, tombs, suites, topology.Geysers, keyed, animals), bounds: survey.Bounds, cells: survey.Cells}
				if !inputs.same(r.planInputs) {
					reason := layoutReasons(map[string]bool{"outgrown": outgrown, "terrain": inputs.bounds != r.planInputs.bounds || !slices.Equal(inputs.cells, r.planInputs.cells), "pawns": int(pawns) != r.planPawns, "research": research, "tomb": tomb, "suite": suite, "throne": throne, "children": children, "gear": gear, "core": core, "outskirts": outskirts, "yard": yard})
					err = r.replanLayout(ctx, snapshot, tick, layout.Plan, survey, growth, animals, int(pawns), tombs, layoutTier(*projection), reason, topology.Geysers, policy.EmptiedRetiringWings(layout.Plan, projection.Rooms, projection.Facts.Sleeping), suites)
					if err == nil {
						r.planGrownFor, r.planPawns = grown, int(pawns)
						r.planInputs, replanned = inputs, true
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
				// The recorded replan is the plan the same inputs grow next.
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
// (#817, bridge.Client.DrawOverlay).
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
	key := fmt.Sprint("v2@", layout.Tick)
	if key == r.overlayKey && tick >= r.overlayDrawn && tick-r.overlayDrawn < overlayRedrawEvery {
		return
	}
	layer, fields := policy.SplitFields(layout.Plan.Overlay(projection.Bounds))
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), fieldLayer, fields, len(fields.Layers) > 0); err != nil {
		return
	}
	_, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, layer, true)
	if err != nil {
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
}

// layoutPlan reads the v2 layout plan (#783). A saved plan that no longer
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
// geysers and records it.
func (r *Rounder) deriveLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, survey policy.MapSurvey, pawns int, tier policy.BuildTier, geysers []policy.PowerGeyser, animals int) error {
	plan, known := policy.DeriveLayoutPlan(survey, pawns, tier, geysers, animals).Value()
	if !known {
		telemetry.Decide(ctx, layoutPlanDecision("skipped", "no_core", pawns, "", nil))
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, plan); err != nil {
		return err
	}
	telemetry.Decide(ctx, layoutPlanDecision("planned", "", pawns, plan.Summary(), nil))
	return nil
}

// replanLayout grows the recorded v2 plan over a fresh survey and records
// it when it changed.
func (r *Rounder) replanLayout(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan, survey policy.MapSurvey, growth policy.RoomGrowth, animals, pawns, tombs int, tier policy.BuildTier, reason string, geysers []policy.PowerGeyser, emptied map[domain.Cell]bool, suites []float64) error {
	next, changed, unplaced := policy.ReplanLayoutWithRooms(plan, survey, growth, animals, pawns, tombs, tier, geysers, emptied, suites...)
	r.logNoRoom(ctx, pawns, unplaced)
	if !changed {
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, next); err != nil {
		return err
	}
	extra := map[string]any{"tomb_short": max(tombs-next.TombRooms(), 0)}
	telemetry.Decide(ctx, layoutPlanDecision("replanned", reason, pawns, next.Summary(), extra))
	return nil
}

// logNoRoom writes a layout_plan refused/no_room row when a replan left
// rooms unplaced (the map had no site for them), once per distinct
// unplaced set so a plan that stays full does not repeat the row each
// round. A replan that places everything clears the memory.
func (r *Rounder) logNoRoom(ctx context.Context, colonists int, unplaced error) {
	if unplaced == nil {
		r.noRoomLogged = ""
		return
	}
	text := unplaced.Error()
	if text == r.noRoomLogged {
		return
	}
	r.noRoomLogged = text
	telemetry.Decide(ctx, layoutPlanDecision("refused", "no_room", colonists, "", map[string]any{"unplaced": text}))
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

// logSuiteClaims logs the suite claims (pawn, reason, target) whenever the
// set changes (#1257), so a run shows who is owed a suite and why.
func (r *Rounder) logSuiteClaims(ctx context.Context, claims []policy.SuiteClaim) {
	line := "none"
	if len(claims) > 0 {
		line = ""
		for i, c := range claims {
			if i > 0 {
				line += " "
			}
			line += fmt.Sprintf("%s:%s:%g", c.Pawn, c.Reason, c.Target)
		}
	}
	if line == r.suiteClaimsLogged || r.suiteClaimsLogged == "" && line == "none" {
		return
	}
	r.suiteClaimsLogged = line
	telemetry.Decide(ctx, telemetry.Decision{Kind: "layout_plan", Component: "layout", Verdict: "claimed", Target: "suites", Attrs: map[string]any{"claims": len(claims), "summary": line}})
}

// heatRedrawEvery is how often the traffic heat layers are redrawn (one
// game hour).
const heatRedrawEvery domain.Tick = 2500

// drawHeatOverlay redraws a "heat.<layer>" overlay layer per traffic layer
// (#817) from the census's busiest cells, on its own hourly cadence; with
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

// layoutTier is the build tier new bedroom wings are sized for (#1214); an
// unknown tier reads Camp.
func layoutTier(projection observation.ColonyProjection) policy.BuildTier {
	tier, _ := projection.BuildTier.Value()
	return tier
}
