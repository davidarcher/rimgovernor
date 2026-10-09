package bridge

import (
	"context"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PlanningWindowRect is the planning window on a map of bounds: the whole
// map. The window was the colony centre +/- 22 cells widened to the layout
// plan's extent, and a plan first derived after the window was read left
// the planners that anchor on its core looking at cells round the landing
// only (a butcher spot placed 70 cells from the core). plan is kept for
// the callers' signature; it does not narrow the read.
func PlanningWindowRect(bounds policy.Bounds, plan policy.Rectangle) policy.Rectangle {
	return policy.Rectangle{Width: bounds.Width, Height: bounds.Height}
}

// PlanningWindow is the planning window's site cells inside Region, from
// the snapshot stream's whole-map grid, with the context of the frame that
// carried it. Fogged cells are never listed; Filtered counts them.
type PlanningWindow struct {
	Context  *c.ObservationContext
	Region   policy.Rectangle
	Cells    []policy.SiteCell
	Filtered uint64
}

// ReadPlanningWindow reads the planning window rect from the snapshot
// stream's whole-map grid; a client without a stream serves none
// (ErrUnavailable), and the caller falls back to whatever window it holds.
func (client *Client) ReadPlanningWindow(ctx context.Context, identity *c.Identity, rect policy.Rectangle) (PlanningWindow, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return PlanningWindow{}, Result{}, err
	}
	if rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1 {
		return PlanningWindow{}, Result{}, contract("invalid planning window rect")
	}
	if client.frames == nil {
		return PlanningWindow{}, Result{}, fmt.Errorf("%w: the planning window is served only by the snapshot stream", ErrUnavailable)
	}
	window, err := client.frameWindow(ctx, identity, rect)
	return window, Result{}, err
}

// SortSiteCells orders site cells row-major (z, then x), the order a
// planning window lists them.
func SortSiteCells(cells []policy.SiteCell) {
	sort.Slice(cells, func(i, j int) bool {
		a, b := cells[i].Cell, cells[j].Cell
		if a.Z != b.Z {
			return a.Z < b.Z
		}
		return a.X < b.X
	})
}

// CellPresence is whether a named cell feature is present: a value means
// present, a not-applicable issue or an applied field without a value
// means absent, and an unread field stays unknown.
func CellPresence(value *string, issues []*o.ReadIssue, field string, applied bool) domain.Fact[bool] {
	if value != nil {
		return domain.Known(true)
	}
	for _, issue := range issues {
		if issue.GetField() == field {
			if issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
				return domain.Known(false)
			}
			return domain.Unknown[bool]()
		}
	}
	if applied {
		return domain.Known(false)
	}
	return domain.Unknown[bool]()
}
