package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

// statusRedrawEvery is how often unchanged strip rows are resent (one game
// hour).
const statusRedrawEvery domain.Tick = 2500

// refusalLayer is the native overlay layer the refusal markers draw on.
const refusalLayer = "refusals"

// StatusStripNative draws the in-game status strip (#823,
// bridge.Client.DrawStatusStrip).
type StatusStripNative interface {
	DrawStatusStrip(context.Context, *c.Identity, []policy.StatusRow, bool) (*p.StatusStripApplied, bridge.Result, error)
}

// statusStripState is the last strip and refusal layer sent.
type statusStripState struct {
	key     string
	drawn   domain.Tick
	cleared bool
}

// drawStatusStrip pushes the status strip and the refusal markers (#823)
// after a review, gated by the layout overlay flag: only when the rows
// changed or an hour passed; with the flag off it hides both once. Output
// only: a failure is logged, never fatal.
func (r *RoutineReviewer) drawStatusStrip(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, result store.RoutineReviewResult) {
	strip, ok := r.native.(StatusStripNative)
	overlay, overlayOK := r.native.(LayoutOverlayNative)
	if !ok || !overlayOK {
		return
	}
	identity := controlIdentity(snapshot)
	if !r.layoutOverlay {
		if !r.strip.cleared {
			if _, _, err := strip.DrawStatusStrip(ctx, identity, nil, false); err != nil {
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
	refusals := policy.LiveRefusals(r.liveRefusals(ctx, result.Goals), tick)
	f := projection.Facts
	rows := policy.StatusRows(policy.StatusInput{Stage: result.Review.Stage, Progress: result.Review.Progress, Colonists: f.Colonists, FoodDays: f.FoodDays, Wood: f.Wood, WoodFloor: result.Review.WoodFloor, Emergency: result.Emergency, Refusals: refusals})
	key := fmt.Sprint(rows, refusals)
	if key == r.strip.key && tick >= r.strip.drawn && tick-r.strip.drawn < statusRedrawEvery {
		return
	}
	if _, _, err := strip.DrawStatusStrip(ctx, identity, rows, true); err != nil {
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

// liveRefusals are the active goals' still-pending actions native refused
// that name a cell: a refusal clears when its plan retires or the action
// leaves Pending. A plan that does not load is skipped.
func (r *RoutineReviewer) liveRefusals(ctx context.Context, goals []store.GoalState) []policy.RefusalMarker {
	var out []policy.RefusalMarker
	for _, goal := range goals {
		for _, method := range goal.Methods {
			plan, err := r.player.journal.LoadPlan(ctx, method.Plan)
			if err != nil || plan.Retired {
				continue
			}
			for _, progress := range plan.Progress {
				v := progress.View()
				receipt, known := v.Receipt.Value()
				if !known || receipt != domain.ReceiptRefused || v.Stage != domain.Pending {
					continue
				}
				cell, ok := actionCell(progress.Action())
				if !ok {
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
	return out
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
