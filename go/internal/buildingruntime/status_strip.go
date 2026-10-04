package buildingruntime

import (
	"context"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

// statusRedrawEvery is how often unchanged strip rows are resent (one game
// hour).
const statusRedrawEvery domain.Tick = domain.TicksPerHour

// refusalLayer is the native overlay layer the refusal markers draw on.
const refusalLayer = "refusals"

// StatusStripNative draws the in-game status strip (#823,
// bridge.Client.DrawStatusStrip).
type StatusStripNative interface {
	DrawStatusStrip(context.Context, *c.Identity, []policy.StatusRow, []policy.PanelAction, bool) (*p.StatusStripApplied, bridge.Result, error)
}

// statusStripState is the last strip and refusal layer sent.
type statusStripState struct {
	key     string
	drawn   domain.Tick
	cleared bool
}

// drawStatusStrip pushes the status strip, the panel's buttons (#957) and
// the refusal markers (#823) after a review, gated by the layout overlay flag: only when the rows
// changed or an hour passed; with the flag off it hides both once. Output
// only: a failure is logged, never fatal.
func (r *Rounder) drawStatusStrip(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, result store.RoundsResult) {
	strip, ok := r.native.(StatusStripNative)
	overlay, overlayOK := r.native.(LayoutOverlayNative)
	if !ok || !overlayOK {
		return
	}
	identity := controlIdentity(snapshot)
	if !r.layoutOverlay {
		if !r.strip.cleared {
			if _, _, err := strip.DrawStatusStrip(ctx, identity, nil, nil, false); err != nil {
				clockSchedulerLog("status strip not cleared: %v", err)
				return
			}
			if _, _, err := overlay.DrawOverlay(ctx, identity, refusalLayer, policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("refusal overlay not cleared: %v", err)
				return
			}
			r.strip.cleared = true
		}
		return
	}
	tick := projection.Identity.Tick
	marked, cells := r.planMarks(ctx, result.Goals, result.Projects, result.Incidents)
	refusals := policy.LiveRefusals(marked, tick)
	f := projection.Facts
	// An invalid reserve read leaves Stock unknown and drops the row.
	medicine, _ := policy.ReviewMedicalReserve(f.MedicalReserve, false, r.policy.MedicalReserve)
	target, _ := medicine.Target.Value()
	rows := policy.StatusRows(policy.StatusInput{Stage: result.Review.Stage, Progress: result.Review.Progress, Colonists: f.Colonists, FoodDays: f.FoodDays, Wood: f.Wood, WoodFloor: result.Review.WoodFloor, Emergency: result.Emergency, Refusals: refusals, Pause: r.pause, Medicine: medicine.Stock, MedicineTarget: target, GoalCells: cells, Outlook: f.Outlook, Incidents: openIncidents(result.Incidents)})
	layoutRows, actions := r.layoutPanel(projection)
	rows = append(rows, layoutRows...)
	if plan, pk := projection.LayoutPlan.Value(); pk {
		if rooms, rk := projection.Rooms.Value(); rk {
			if sleeping, sk := f.Sleeping.Value(); sk {
				rows = append(rows, policy.BedroomRows(plan, rooms, sleeping, bedroomTargets(*projection))...)
			}
		}
	}
	key := fmt.Sprint(rows, refusals, actions)
	if key == r.strip.key && tick >= r.strip.drawn && tick-r.strip.drawn < statusRedrawEvery {
		return
	}
	if _, _, err := strip.DrawStatusStrip(ctx, identity, rows, actions, true); err != nil {
		clockSchedulerLog("status strip not drawn: %v", err)
		return
	}
	markers := policy.RefusalOverlay(refusals, tick)
	if _, _, err := overlay.DrawOverlay(ctx, identity, refusalLayer, markers, len(markers.Layers) > 0); err != nil {
		clockSchedulerLog("refusal overlay not drawn: %v", err)
		return
	}
	r.strip = statusStripState{key: key, drawn: tick}
}

// drawReviewing shows "reviewing colony..." on the panel until the first
// review draws its rows (#1251): startup rebuilds and reviews for a while
// before the panel has anything to say, which otherwise reads as a hang.
// ASCII only (#600). Output only: a failure is logged, never fatal.
func (r *Rounder) drawReviewing(ctx context.Context, identity *c.Identity) {
	strip, ok := r.native.(StatusStripNative)
	if !ok || !r.layoutOverlay || r.strip.key != "" || identity == nil {
		return
	}
	rows := []policy.StatusRow{{Key: "reviewing", Text: "reviewing colony..."}}
	if _, _, err := strip.DrawStatusStrip(ctx, identity, rows, nil, true); err != nil {
		clockSchedulerLog("status strip not drawn: %v", err)
		return
	}
	r.strip.key = reviewingKey
}

// reviewingKey marks the strip as holding only the startup row; it never
// equals a review's key, so the first review redraws.
const reviewingKey = "reviewing"

// openIncidents is the incidents the strip lists (#1025).
func openIncidents(states []store.IncidentState) []domain.Incident {
	out := make([]domain.Incident, 0, len(states))
	for _, s := range states {
		out = append(out, s.Incident)
	}
	return out
}

// planMarks reads the active goals' and open incidents' (#1078) plans
// once for the strip: the
// still-pending actions native refused that name a cell (a refusal clears
// when its plan retires or the action leaves Pending), and each goal's
// target, the cell of its first open action that names one (#847). A plan
// that does not load is skipped.
func (r *Rounder) planMarks(ctx context.Context, goals []store.GoalState, projects []store.ProjectState, incidents []store.IncidentState) ([]policy.RefusalMarker, map[policy.ConcernID]domain.Cell) {
	type owner struct {
		id    policy.ConcernID
		plans []domain.PlanID
	}
	var owners []owner
	for _, goal := range goals {
		o := owner{id: policy.ConcernID(goal.Goal.ID)}
		for _, method := range goal.Methods {
			o.plans = append(o.plans, method.Plan)
		}
		owners = append(owners, o)
	}
	for _, project := range projects {
		o := owner{id: policy.ConcernID(project.Project.ID)}
		for _, method := range project.Methods {
			o.plans = append(o.plans, method.Plan)
		}
		owners = append(owners, o)
	}
	for _, incident := range incidents {
		o := owner{id: incident.Incident.Kind}
		for _, method := range incident.Methods {
			o.plans = append(o.plans, method.Plan)
		}
		owners = append(owners, o)
	}
	var out []policy.RefusalMarker
	cells := map[policy.ConcernID]domain.Cell{}
	for _, o := range owners {
		for _, planID := range o.plans {
			plan, err := r.player.journal.LoadPlan(ctx, planID)
			if err != nil || plan.Retired {
				continue
			}
			for _, progress := range plan.Progress {
				v := progress.View()
				cell, ok := actionCell(progress.Action())
				if !ok {
					continue
				}
				id := o.id
				if _, marked := cells[id]; !marked && domain.GoalWorkOpen([]domain.Progress{progress}) {
					cells[id] = cell
				}
				receipt, known := v.Receipt.Value()
				if !known || receipt != domain.ReceiptRefused || v.Stage != domain.Pending {
					continue
				}
				label := string(progress.Action().Kind())
				if reason, known := v.UnsuccessfulReason.Value(); known {
					label += " " + string(reason)
				}
				out = append(out, policy.RefusalMarker{Label: label, Cell: cell, Tick: v.Tick})
			}
		}
	}
	return out, cells
}

// clockPause is who stopped the clock the step read and why (#847): zero
// while it runs, the player runs it by hand, or only the governor's own
// window boundary stopped it; else the player's pause, a letter or
// dialog, a colony watch hold, or a governor fault.
func clockPause(status *k.Status) policy.ClockPause {
	stopped := status.GetStopped()
	if stopped == nil || clockPlayerRunning(status) {
		return policy.ClockPause{}
	}
	reason := stopped.GetReason()
	name := strings.ToLower(strings.TrimPrefix(reason.String(), "STOP_REASON_"))
	switch reason {
	case k.StopReason_STOP_REASON_UNSPECIFIED, k.StopReason_STOP_REASON_REQUESTED_PAUSE, k.StopReason_STOP_REASON_TICK_BUDGET, k.StopReason_STOP_REASON_WATCH_LATCHED:
		return policy.ClockPause{}
	case k.StopReason_STOP_REASON_EXTERNAL_PAUSE, k.StopReason_STOP_REASON_FORCE_PAUSED, k.StopReason_STOP_REASON_EXTERNAL_SPEED_CHANGED:
		return policy.ClockPause{By: "player", Reason: name}
	case k.StopReason_STOP_REASON_LETTER_PAUSE, k.StopReason_STOP_REASON_NOTIFICATION_BATCH, k.StopReason_STOP_REASON_DIALOG_PAUSE, k.StopReason_STOP_REASON_COLONY_NAMING:
		return policy.ClockPause{By: "letter", Reason: name, Held: true}
	case k.StopReason_STOP_REASON_SESSION_CHANGED, k.StopReason_STOP_REASON_UNAVAILABLE, k.StopReason_STOP_REASON_LEASE_EXPIRED, k.StopReason_STOP_REASON_WATCHER_ERROR, k.StopReason_STOP_REASON_EVENT_JOURNAL_ERROR, k.StopReason_STOP_REASON_START_REFUSED:
		return policy.ClockPause{By: "governor", Reason: name, Held: true}
	}
	// The colony watches: hostiles, a downed or injured colonist, a hunt.
	return policy.ClockPause{By: "hold", Reason: name, Held: true}
}

// actionCell is the map cell an action targets, for the kinds that name one.
func actionCell(a domain.Action) (domain.Cell, bool) {
	if v, ok := a.Building(); ok {
		return v.Cell(), true
	}
	if v, ok := a.CutPlant(); ok {
		return v.Cell(), true
	}
	if v, ok := a.Deconstruction(); ok {
		return v.Cell(), true
	}
	if v, ok := a.Excavation(); ok {
		return v.Cell(), true
	}
	if v, ok := a.Repair(); ok {
		return v.Cell(), true
	}
	if v, ok := a.Clean(); ok {
		return v.Cell(), true
	}
	if v, ok := a.MoveBuilding(); ok {
		return v.Cell(), true
	}
	if v, ok := a.Movement(); ok {
		return v.Destination(), true
	}
	return domain.Cell{}, false
}
