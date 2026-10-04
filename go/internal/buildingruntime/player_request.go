package buildingruntime

import (
	"context"
	"fmt"
	"reflect"
	"regexp"
	"strconv"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// Player requests (#957): a button on the in-game status panel publishes a
// clock PlayerRequest event naming the panel action the reviewer offered
// (drawStatusStrip); the poll hands each fresh one to the routine reviewer,
// which acts on it at its next review. The layout actions keep a replan
// proposal beside the saved plan in memory only: a reload or rewind drops
// it, and so does a change of the saved plan under it.

// The panel actions the reviewer offers.
const (
	panelReplanLayout  = "replan_layout"
	panelApplyLayout   = "apply_layout"
	panelDiscardLayout = "discard_layout"
)

// proposalLayer is the native overlay layer a layout proposal draws on.
const proposalLayer = "proposal"

// layoutNoteFor is how long a replan's outcome note stays on the panel
// (one game hour).
const layoutNoteFor domain.Tick = domain.TicksPerHour

// playerRequest is one button press.
type playerRequest struct {
	Action, ID string
}

// playerRequests is the queue between the poll and the reviewer.
type playerRequests struct {
	mu      sync.Mutex
	pending []playerRequest
}

func (q *playerRequests) push(requests []playerRequest) {
	if len(requests) == 0 {
		return
	}
	q.mu.Lock()
	q.pending = append(q.pending, requests...)
	q.mu.Unlock()
}

func (q *playerRequests) take() []playerRequest {
	q.mu.Lock()
	defer q.mu.Unlock()
	out := q.pending
	q.pending = nil
	return out
}

// clockPlayerRequests are the page's player requests from the world the
// page reads, past history (native's newest cursor when this process first
// read events: a press before it started is not acted on).
func clockPlayerRequests(page *k.EventsPage, history int64) []playerRequest {
	var out []playerRequest
	for _, event := range page.GetEvents() {
		v, ok := event.Event.(*k.Event_PlayerRequest)
		if !ok || event.GetCursor() <= history || clock.OtherWorld(event, page) {
			continue
		}
		out = append(out, playerRequest{Action: v.PlayerRequest.GetAction(), ID: v.PlayerRequest.GetRequestId()})
	}
	return out
}

// layoutProposal is a replanned layout awaiting the player's Apply or
// Discard: the world and tick it was made at, and the saved plan's tick
// it was made against.
type layoutProposal struct {
	Plan  policy.LayoutPlan
	World store.World
	Tick  domain.Tick
	Base  domain.Tick
}

// current reports the proposal still stands: same world, no rewind past
// it, and the saved plan it was made against still the newest.
func (p *layoutProposal) current(world store.World, tick domain.Tick, layout store.LayoutPlanRecord, haveLayout bool) bool {
	return p != nil && p.World == world && tick >= p.Tick && haveLayout && layout.Tick == p.Base
}

// servePlayerRequests drops a proposal that no longer stands, then acts on
// the pending requests in order and returns the saved plan as they leave
// it. A request that cannot be served is logged and noted on the panel,
// never an error.
func (r *RoutineReviewer) servePlayerRequests(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) (store.LayoutPlanRecord, bool, error) {
	tick := projection.Identity.Tick
	world := playerWorld(snapshot)
	if r.proposal != nil && !r.proposal.current(world, tick, layout, haveLayout) {
		clockEvent(ctx, "layout", "layout_proposal_dropped", "layout proposal dropped: the world, tick or saved plan moved", "tick", int64(tick))
		r.proposal = nil
	}
	if r.note.tick > tick {
		r.note = layoutNote{}
	}
	for _, request := range r.requests.take() {
		clockEvent(ctx, "layout", "player_request", "player request "+request.Action, "action", request.Action, "request_id", request.ID, "tick", int64(tick))
		switch request.Action {
		case panelReplanLayout:
			r.proposeLayout(ctx, snapshot, projection, layout, haveLayout)
		case panelApplyLayout:
			if r.proposal == nil {
				r.noteLayout(tick, "nothing to apply")
				continue
			}
			if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, r.proposal.Plan); err != nil {
				return layout, haveLayout, err
			}
			clockEvent(ctx, "layout", "layout_apply", "layout proposal applied "+r.proposal.Plan.Summary(), "tick", int64(tick))
			r.proposal = nil
			r.noteLayout(tick, "applied")
			var err error
			if layout, haveLayout, err = r.layoutPlan(ctx, snapshot, tick); err != nil {
				return layout, haveLayout, err
			}
		case panelDiscardLayout:
			if r.proposal != nil {
				clockEvent(ctx, "layout", "layout_discard", "layout proposal discarded", "tick", int64(tick))
			}
			r.proposal = nil
			r.noteLayout(tick, "discarded")
		default:
			clockSchedulerLog("player request %s ignored: unknown action", request.Action)
		}
	}
	return layout, haveLayout, nil
}

// proposeLayout replans the saved layout fresh (policy.ReplanFresh) and
// keeps the result as the proposal when it differs.
func (r *RoutineReviewer) proposeLayout(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) {
	tick := projection.Identity.Tick
	refuse := func(why string) {
		clockEvent(ctx, "layout", "layout_replan_refused", "layout replan refused: "+why, "tick", int64(tick))
		r.noteLayout(tick, why)
	}
	if !haveLayout {
		refuse("no layout plan yet")
		return
	}
	pawns, known := projection.Facts.Colonists.Value()
	if !known {
		refuse("colonists unknown")
		return
	}
	built, known := r.layoutOccupied(ctx, *projection, layout.Plan)
	if !known {
		refuse("building census or plan catalog unavailable")
		return
	}
	native, ok := r.native.(MapSurveyNative)
	if !ok {
		refuse("map survey unavailable")
		return
	}
	survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds)
	if err != nil {
		refuse(fmt.Sprintf("map survey unavailable: %v", err))
		return
	}
	topology, _ := projection.PowerPlanning.Value()
	next, known := policy.ReplanFresh(layout.Plan, survey, built, int(pawns), layout.Plan.TombRooms(), layoutTier(*projection), topology.Geysers, projection.Facts.PenAnimals()).Value()
	if !known {
		refuse("no room for a core")
		return
	}
	if reflect.DeepEqual(next, layout.Plan) {
		r.proposal = nil
		r.noteLayout(tick, "no change")
		return
	}
	r.proposal = &layoutProposal{Plan: next, World: playerWorld(snapshot), Tick: tick, Base: layout.Tick}
	r.note = layoutNote{}
	added, removed := policy.LayoutProposalDiff(layout.Plan, next)
	clockEvent(ctx, "layout", "layout_proposal", fmt.Sprintf("layout proposal +%d rooms -%d rooms %s", added, removed, next.Summary()), "added", added, "removed", removed, "tick", int64(tick))
}

// layoutOccupied is occupiedCells plus the walls of the planned rooms an
// open journal plan is working on (roomMethodOrigins, #1958). Unknown when
// the census or the plan catalog is.
func (r *RoutineReviewer) layoutOccupied(ctx context.Context, projection observation.ColonyProjection, plan policy.LayoutPlan) (map[domain.Cell]bool, bool) {
	cells, known := occupiedCells(projection)
	if !known {
		return nil, false
	}
	plans, err := r.player.journal.LoadPlans(ctx, 256)
	if err != nil {
		clockSchedulerLog("layout occupancy unknown, plan catalog: %v", err)
		return nil, false
	}
	for c := range policy.InFlightRoomCells(plan, roomMethodOrigins(plans)) {
		cells[c] = true
	}
	return cells, true
}

// roomMethodPattern matches the method ids of the plans keyed by a planned
// room's interior origin: its dig, shell, bedroom steps, and the tomb and
// jail pieces.
var roomMethodPattern = regexp.MustCompile(`^(?:plan-dig-[^-]+-[^-]+|bedroom-[^-]+|[^-]+-shell|(?:tomb|jail)-place)-(\d+)-(\d+)(?:-.*)?$`)

// roomMethodOrigins are the interior origins of the planned rooms an open
// (not retired) plan's method works on.
func roomMethodOrigins(plans []store.PlanState) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, p := range plans {
		if p.Retired {
			continue
		}
		if m := roomMethodPattern.FindStringSubmatch(string(p.Method)); m != nil {
			x, xerr := strconv.ParseInt(m[1], 10, 32)
			z, zerr := strconv.ParseInt(m[2], 10, 32)
			if xerr == nil && zerr == nil {
				out[domain.Cell{X: int32(x), Z: int32(z)}] = true
			}
		}
	}
	return out
}

// occupiedCells are the cells of ours a room must not lose: every census
// building, the full footprint of every blueprint and frame site and of every
// journal claim whose work has not closed gone (the definition's size when
// the projection read it, else the anchor), and every player edifice and
// doorway (a door blueprint or frame included) the planning cells report.
// Both replans read it (ReplanFresh's built set, RoomGrowth.Fixed), so they
// never disagree. Unknown without a complete census.
func occupiedCells(projection observation.ColonyProjection) (map[domain.Cell]bool, bool) {
	census, known := projection.Facts.CurrentConstruction.Value()
	if !known || !census.Colony {
		return nil, false
	}
	sizes := map[string]policy.Bounds{}
	for _, d := range projection.Definitions {
		if size, ok := d.Size.Value(); ok {
			sizes[d.Name] = size
		}
	}
	footprint := func(b domain.Building) []domain.Cell {
		if size, ok := sizes[b.Definition()]; ok {
			if r := policy.OccupiedRect(b.Cell(), domain.Cell{X: size.Width, Z: size.Height}, b.Rotation()); r.Width > 0 && r.Height > 0 {
				return policy.RectangleCells(r)
			}
		}
		return []domain.Cell{b.Cell()}
	}
	built := map[domain.Cell]bool{}
	for _, b := range census.Buildings {
		for _, c := range b.Cells {
			built[c] = true
		}
	}
	for _, s := range census.Sites {
		for _, c := range footprint(s.Building) {
			built[c] = true
		}
	}
	claims, _ := projection.Facts.ConstructionClaims.Value()
	for _, c := range claims {
		if policy.WorkOpen(c.Building, projection.Facts.CurrentConstruction) != policy.BuildingGone {
			for _, cell := range footprint(c.Building) {
				built[cell] = true
			}
		}
	}
	for _, c := range projection.Cells {
		edifice, _ := c.PlayerEdifice.Value()
		door, _ := c.Doorway.Value()
		if edifice != "" || door {
			built[c.Cell] = true
		}
	}
	return built, true
}

// layoutNote is the last layout request's outcome shown on the panel.
type layoutNote struct {
	text string
	tick domain.Tick
}

func (r *RoutineReviewer) noteLayout(tick domain.Tick, text string) {
	r.note = layoutNote{text: text, tick: tick}
}

// layoutPanel is the panel's layout row and buttons: Apply and Discard
// with the proposal's row while one stands, else Replan layout once a plan
// is saved, with the last request's outcome for an hour.
func (r *RoutineReviewer) layoutPanel(projection *observation.ColonyProjection) ([]policy.StatusRow, []policy.PanelAction) {
	tick := projection.Identity.Tick
	current, have := projection.LayoutPlan.Value()
	if r.proposal != nil && have {
		added, removed := policy.LayoutProposalDiff(current, r.proposal.Plan)
		row := policy.StatusRow{Key: "layout", Text: fmt.Sprintf("Layout proposal: +%d rooms, -%d rooms (green added, red dropped)", added, removed), Severity: policy.StatusWarning}
		return []policy.StatusRow{row}, []policy.PanelAction{
			{ID: panelApplyLayout, Label: "Apply", Tip: "Adopt the proposed layout; built rooms stay."},
			{ID: panelDiscardLayout, Label: "Discard", Tip: "Keep the current layout."},
		}
	}
	var rows []policy.StatusRow
	if r.note.text != "" && tick >= r.note.tick && tick-r.note.tick < layoutNoteFor {
		rows = append(rows, policy.StatusRow{Key: "layout", Text: "Layout replan: " + r.note.text, Severity: policy.StatusInfo})
	}
	if !have {
		return rows, nil
	}
	return rows, []policy.PanelAction{{ID: panelReplanLayout, Label: "Replan layout", Tip: "Lay the plan out again around what is built; review it before applying."}}
}

// drawProposalOverlay draws the standing proposal against the saved plan
// on its own layer, or removes the layer once it is gone. Output only: a
// failure is logged, never fatal.
func (r *RoutineReviewer) drawProposalOverlay(ctx context.Context, native LayoutOverlayNative, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord) {
	key := ""
	if r.proposal != nil && r.layoutOverlay {
		key = fmt.Sprint(r.proposal.Tick, "@", r.proposal.Base)
	}
	if key == r.proposalDrawn {
		return
	}
	var overlay policy.LayoutOverlay
	if key != "" {
		overlay = policy.ProposalOverlay(layout.Plan, r.proposal.Plan, projection.Bounds)
	}
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), proposalLayer, overlay, key != "" && len(overlay.Layers) > 0); err != nil {
		clockSchedulerLog("proposal overlay not drawn: %v", err)
		return
	}
	r.proposalDrawn = key
}
