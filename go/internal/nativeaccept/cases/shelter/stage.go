package shelter

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The staging helpers of the retirement and climate cases (#2076): both read
// the layout plan the running controller recorded, stop the service, raise
// finished rooms on the planned rectangles (test/layout_rooms_stage) and
// restart it on the same journal. Staging on the recorded plan, not on a plan
// derived offline, keeps the rooms on the interiors the controller actually
// holds: a demand-grown room (the laboratory) is sited by a scored search the
// offline derivation does not reproduce.

const (
	stageRoomsOp = "test/layout_rooms_stage"
	// planWait is the wall-clock ceiling for the first review that records
	// the plan a case stages on.
	planWait = 6 * time.Minute
)

// waitPlan waits for a review whose latest recorded layout plan satisfies ready
// and returns the record.
func waitPlan(ctx context.Context, service *na.ServiceProcess, ceiling time.Duration, ready func(policy.LayoutPlan) bool) (store.LayoutPlanRecord, error) {
	journal, err := service.Store(ctx)
	if err != nil {
		return store.LayoutPlanRecord{}, err
	}
	var record store.LayoutPlanRecord
	_, err = service.WaitReview(ctx, na.Wait{Ceiling: ceiling}, func(r store.Rounds) bool {
		got, ok, err := journal.LayoutPlan(ctx, r.Snapshot, r.Tick)
		if err != nil || !ok || !ready(got.Plan) {
			return false
		}
		record = got
		return true
	})
	return record, err
}

// roomsOf lists the plan's rooms of role.
func roomsOf(plan policy.LayoutPlan, role policy.ModuleRole) []policy.LayoutRoom {
	var out []policy.LayoutRoom
	for _, r := range plan.AllRooms() {
		if r.Role == role {
			out = append(out, r)
		}
	}
	return out
}

// stageRooms raises each room finished, roofed and walled on its planned
// interior and door.
func stageRooms(ctx context.Context, h *na.Harness, label string, rooms ...policy.LayoutRoom) (map[string]any, error) {
	parts := make([]string, 0, len(rooms))
	for _, r := range rooms {
		if r.Dug {
			return nil, fmt.Errorf("planned %s room %+v is dug from rock: the stage raises open-ground rooms only", r.Role, r.Interior)
		}
		in := r.Interior
		parts = append(parts, fmt.Sprintf("%d,%d,%d,%d,%d,%d", in.X, in.Z, in.Width, in.Height, r.Door.X, r.Door.Z))
	}
	reply, err := h.Call(ctx, label, stageRoomsOp, map[string]any{"rooms": strings.Join(parts, ";")})
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return reply, fmt.Errorf("%s did not leave every room enclosed and roofed: %#v", stageRoomsOp, reply)
	}
	return reply, nil
}

// inside reports whether c lies in r.
func inside(r policy.Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height
}

// intersect is the overlap of a and b, zero-sized when they are apart.
func intersect(a, b policy.Rectangle) policy.Rectangle {
	x, z := max(a.X, b.X), max(a.Z, b.Z)
	w, d := min(a.X+a.Width, b.X+b.Width)-x, min(a.Z+a.Height, b.Z+b.Height)-z
	if w <= 0 || d <= 0 {
		return policy.Rectangle{}
	}
	return policy.Rectangle{X: x, Z: z, Width: w, Height: d}
}

// buildingCells reads the cells the building id occupies; found is false when
// no such building stands.
func buildingCells(ctx context.Context, h *na.Harness, identity map[string]any, label, id string) (cells []domain.Cell, found bool, err error) {
	reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "ids": []string{id}})
	if err != nil {
		return nil, false, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, false, err
	}
	rows := na.AsSlice(observed["buildings"])
	if len(rows) == 0 {
		return nil, false, nil
	}
	row, _ := na.AsMap(rows[0])
	return na.RectCells(row["occupied"]), true, nil
}
