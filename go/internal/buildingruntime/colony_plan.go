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
func (r *RoutineReviewer) reviewLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
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
	tombs := 1
	tomb := haveLayout && tombsFull(layout.Plan, *projection)
	if tomb {
		tombs = layout.Plan.TombRooms() + 1
	}
	tomb = tomb && hourly
	// A pawn owed a suite no planned suite answers (#1216): grow the suite
	// wing, or a suite below its owner's target outward (#1218), at most
	// once an hour.
	var suites []float64
	if haveLayout {
		var claims []policy.SuiteClaim
		suites, claims = suiteTargets(*projection, layout.Plan)
		r.logSuiteClaims(ctx, claims)
	}
	suite := haveLayout && policy.SuitesOwed(layout.Plan, suites) && hourly
	// A colonist who holds or can claim a title that asks for a throne room
	// the plan lacks (#1601): grow one sized to the title's area.
	var growth policy.RoomGrowth
	if need, owed := throneNeed(*projection); haveLayout {
		growth.ThroneArea = policy.ThroneAreaOwed(layout.Plan, need, owed)
	}
	throne := growth.ThroneArea > 0 && hourly
	// A baby, toddler or child owed a nursery, playroom or classroom the
	// plan lacks (#1680): grow it, sized to its furniture.
	if haveLayout {
		growth.Child = policy.ChildRoomsOwed(layout.Plan, childRoomNeeds(*projection), furnitureDefinitions(*projection))
	}
	children := len(growth.Child) > 0 && hourly
	// Stored gear outgrew its zone (#1773): the storage planner's demand adds
	// the armory or wardrobe the plan lacks, at most once an hour.
	if demand := r.stockpiles.gearDemand(stockpileWorld(snapshot)); haveLayout && len(policy.GearRoomsOwed(layout.Plan, demand)) > 0 {
		growth.Gear = demand
	}
	gear := growth.Gear != (policy.GearRoomDemand{}) && hourly
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || missing || terrain || research || tomb || suite || throne || children || gear) {
		replanned := false
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			clockSchedulerLog("layout plan check deferred, map survey unavailable: %v", err)
		} else {
			r.planChecked, r.planSurveyed = tick, true
			topology, _ := projection.PowerPlanning.Value()
			if !haveLayout {
				err = r.deriveLayoutPlan(ctx, snapshot, tick, survey, int(pawns), layoutTier(*projection), topology.Geysers, animals)
				r.planGrownFor, r.planPawns, r.planInputs = grown, int(pawns), layoutInputs{}
			} else {
				inputs := layoutInputs{planTick: layout.Tick, key: fmt.Sprint(grown, pawns, tombs, suites, topology.Geysers, growth, animals), bounds: survey.Bounds, cells: survey.Cells}
				if !inputs.same(r.planInputs) {
					reason := layoutReasons(map[string]bool{"outgrown": outgrown, "terrain": inputs.bounds != r.planInputs.bounds || !slices.Equal(inputs.cells, r.planInputs.cells), "pawns": int(pawns) != r.planPawns, "research": research, "tomb": tomb, "suite": suite, "throne": throne, "children": children, "gear": gear})
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
	if layout, haveLayout, err = r.servePlayerRequests(ctx, snapshot, projection, layout, haveLayout); err != nil {
		return err
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
func (r *RoutineReviewer) drawLayoutOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	r.drawHeatOverlay(ctx, native, snapshot, projection)
	r.drawProposalOverlay(ctx, native, snapshot, projection, layout)
	tick := projection.Identity.Tick
	if !r.layoutOverlay || !haveLayout {
		if !r.overlayCleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("layout overlay not cleared: %v", err)
				return
			}
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), fieldLayer, policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("field overlay not cleared: %v", err)
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
		clockSchedulerLog("field overlay not drawn: %v", err)
		return
	}
	applied, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, layer, true)
	if err != nil {
		clockSchedulerLog("layout overlay not drawn: %v", err)
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
	clockSchedulerLog("layout overlay drawn layers=%d cells=%d", applied.GetLayers(), applied.GetCells())
}

// layoutPlan reads the v2 layout plan (#783). A saved plan that no longer
// decodes or validates reads as none, logged once per process, so the next
// survey derives a fresh one.
func (r *RoutineReviewer) layoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (store.LayoutPlanRecord, bool, error) {
	record, ok, err := r.player.journal.LayoutPlan(ctx, snapshot, tick)
	if err == nil && record.Invalid && !r.layoutInvalidLogged {
		r.layoutInvalidLogged = true
		clockSchedulerLog("saved layout plan from tick %d is invalid, replanning", record.Tick)
	}
	return record, ok, err
}

// deriveLayoutPlan lays a fresh v2 plan over survey and the reported
// geysers and records it.
func (r *RoutineReviewer) deriveLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, survey policy.MapSurvey, pawns int, tier policy.BuildTier, geysers []policy.PowerGeyser, animals int) error {
	plan, known := policy.DeriveLayoutPlan(survey, pawns, tier, geysers, animals).Value()
	if !known {
		clockSchedulerLog("map survey holds no core for the layout plan")
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, plan); err != nil {
		return err
	}
	clockEvent(ctx, "layout", "layout_plan", fmt.Sprintf("layout plan for %d colonists %s", pawns, plan.Summary()), "colonists", pawns)
	return nil
}

// replanLayout grows the recorded v2 plan over a fresh survey and records
// it when it changed.
func (r *RoutineReviewer) replanLayout(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan, survey policy.MapSurvey, growth policy.RoomGrowth, animals, pawns, tombs int, tier policy.BuildTier, reason string, geysers []policy.PowerGeyser, emptied map[domain.Cell]bool, suites []float64) error {
	next, changed := policy.ReplanLayoutWithRooms(plan, survey, growth, animals, pawns, tombs, tier, geysers, emptied, suites...)
	if next.TombRooms() < tombs {
		clockSchedulerLog("layout plan holds no room for tomb %d", tombs)
	}
	if !changed {
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, next); err != nil {
		return err
	}
	clockEvent(ctx, "layout", "layout_replan", fmt.Sprintf("layout plan replanned for %d colonists reason=%s %s", pawns, reason, next.Summary()), "colonists", pawns, "reason", reason)
	return nil
}

// layoutReasons names the triggers that fired, sorted, for the
// layout_replan event.
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
func (r *RoutineReviewer) logSuiteClaims(ctx context.Context, claims []policy.SuiteClaim) {
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
	clockEvent(ctx, "layout", "suite_claims", "suite claims "+line, "claims", len(claims))
}

// heatRedrawEvery is how often the traffic heat layers are redrawn (one
// game hour).
const heatRedrawEvery domain.Tick = 2500

// drawHeatOverlay redraws a "heat.<layer>" overlay layer per traffic layer
// (#817) from the census's busiest cells, on its own hourly cadence; with
// the overlay off it removes them once. Output only, like the layout.
func (r *RoutineReviewer) drawHeatOverlay(ctx context.Context, native LayoutOverlayNative, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) {
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
			clockSchedulerLog("heat overlay %s not drawn: %v", layer, err)
			return
		}
	}
	r.heatDrawn, r.heatCleared = tick, !on
}

// tombGrowthRefused reports that a layout survey ran within the last day
// while the projection's plan still has every tomb full (#857): the review
// replans for another tomb whenever they are all full and no survey ran
// that day, and at once after a restart (planSurveyed is unset), so a plan
// still full after it holds no room for one. Nothing is remembered beyond
// the survey tick the reviewer already keeps.
func (r *RoutineReviewer) tombGrowthRefused(tick domain.Tick) bool {
	return r.planSurveyed && r.planChecked <= tick && tick-r.planChecked < layoutReplanEvery
}

// layoutTier is the build tier new bedroom wings are sized for (#1214); an
// unknown tier reads Camp.
func layoutTier(projection observation.ColonyProjection) policy.BuildTier {
	tier, _ := projection.BuildTier.Value()
	return tier
}
