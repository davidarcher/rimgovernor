package zone

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// invalidationWindowTicks bounds the clock window an edit runs under; the
// window is paused as soon as the journal carries the invalidation, so the
// budget only matters when it never arrives.
const invalidationWindowTicks = 1800

// invalidationWait bounds the long polls spent waiting for the journal row.
const invalidationWait = 20 * time.Second

// editUnderEpoch runs execute while a supervised clock window plays and
// returns the observation_invalidated row the native probe journals for
// the zone (#359): the colony family narrowed to the zone's id and one
// rectangle covering cells. The window is paused afterwards; the caller's
// later dispatch runs on a paused game as before.
func editUnderEpoch(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report, zoneID string, cells []map[string]any, execute func() error) (map[string]any, error) {
	supervisor := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: sessionOwner, Report: report}
	if _, err := supervisor.Acquire(ctx, "invalidation-acquire"); err != nil {
		return nil, err
	}
	// The native journal opens lazily; an events read opens it so the status
	// below reports the retained watermark (its reply is irrelevant here).
	if _, err := h.Wire(ctx, "invalidation-prime", "clock_read_events", map[string]any{"identity": identity, "afterCursor": "0", "limit": 1}); err != nil {
		return nil, err
	}
	statusReply, err := h.Wire(ctx, "invalidation-cursor", "clock_read_status", map[string]any{"identity": identity})
	if err != nil {
		return nil, err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return nil, err
	}
	watermark, ok := status["newestCursor"]
	if !ok {
		return nil, fmt.Errorf("invalidation-cursor: clock status reports no journal watermark: %#v", status)
	}
	cursor, err := na.ScenarioInteger(watermark)
	if err != nil {
		return nil, err
	}
	started, err := supervisor.Control(ctx, "start", map[string]any{
		"authority": map[string]any{"identity": identity, "expectedGeneration": na.GrantGeneration(supervisor.Grant),
			"attempt": map[string]any{"controllerSessionId": sessionOwner, "actionId": "zone-invalidation-start", "attemptId": "1"}},
		"speed": "SPEED_NORMAL", "policy": map[string]any{"mode": "WATCH_MODE_COLONY", "healthDropFraction": 0.1, "minHealthFraction": 0.5,
			"hostileWithin": 40, "injuryStopCooldownMs": 0}, "leaseMs": 30000, "maxTicks": invalidationWindowTicks,
	})
	if err != nil {
		return nil, err
	}
	epoch := started["epoch"]
	if err := execute(); err != nil {
		return nil, err
	}
	deadline := time.Now().Add(invalidationWait)
	var invalidated map[string]any
	for invalidated == nil {
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("no observation_invalidated naming zone %s within %s of the edit", zoneID, invalidationWait)
		}
		reply, err := h.Wire(ctx, "invalidation-events", "clock_read_events", map[string]any{"identity": identity, "afterCursor": fmt.Sprint(cursor), "limit": 128, "waitMs": 5000})
		if err != nil {
			return nil, err
		}
		_, page, err := na.Outcome(reply, "page")
		if err != nil {
			return nil, err
		}
		if gap, _ := na.AsBool(page["gap"]); gap {
			return nil, fmt.Errorf("invalidation-events: unexpected journal gap: %#v", page)
		}
		if cursor, err = na.ScenarioInteger(page["nextCursor"]); err != nil {
			return nil, err
		}
		for _, raw := range na.AsSlice(page["events"]) {
			event, _ := na.AsMap(raw)
			if stop, ok := na.AsMap(event["stopped"]); ok {
				return nil, fmt.Errorf("the window stopped (%s) before the journal named the zone edit: %#v", na.AsString(stop["reason"]), stop)
			}
			row, ok := na.AsMap(event["observationInvalidated"])
			if !ok {
				continue
			}
			if na.Contains(asStrings(row["entityIds"]), zoneID) {
				invalidated = row
			}
		}
	}
	if _, err := supervisor.Call(ctx, "pause", map[string]any{"owner": sessionOwner, "epoch": epoch}); err != nil {
		return nil, err
	}
	if err := checkInvalidation(invalidated, zoneID, cells); err != nil {
		return nil, err
	}
	report["zone_invalidation"] = invalidated
	fmt.Printf("PASS observation_invalidated names zone %s over %v\n", zoneID, invalidated["cells"])
	return invalidated, nil
}

// checkInvalidation asserts the row carries the colony family alone, the
// zone as its one entity id and a rectangle that covers every cell the
// edit touched and lies within the zone's fixture footprint.
func checkInvalidation(row map[string]any, zoneID string, cells []map[string]any) error {
	families := asStrings(row["families"])
	if len(families) != 1 || families[0] != "FACT_FAMILY_COLONY" {
		return fmt.Errorf("observation_invalidated families = %v, expected the colony family alone: %#v", families, row)
	}
	if ids := asStrings(row["entityIds"]); len(ids) != 1 || ids[0] != zoneID {
		return fmt.Errorf("observation_invalidated entityIds = %v, expected [%s]: %#v", ids, zoneID, row)
	}
	if !strings.Contains(na.AsString(row["reason"]), zoneID) {
		return fmt.Errorf("observation_invalidated reason does not name the zone: %#v", row)
	}
	rect, ok := na.AsMap(row["cells"])
	if !ok {
		return fmt.Errorf("observation_invalidated carries no cells rectangle: %#v", row)
	}
	minimum, _ := na.AsMap(rect["minimum"])
	maximum, _ := na.AsMap(rect["maximum"])
	minX, minZ := na.AsNumber(minimum["x"]), na.AsNumber(minimum["z"])
	maxX, maxZ := na.AsNumber(maximum["x"]), na.AsNumber(maximum["z"])
	if minX > maxX || minZ > maxZ {
		return fmt.Errorf("observation_invalidated rectangle is not ordered: %#v", rect)
	}
	// The rectangle spans the zone's old and new cells: within the fixture
	// footprint, and covering every fixture cell (the edit restored the
	// full footprint, so the union is the footprint itself).
	fMinX, fMinZ, fMaxX, fMaxZ := na.AsNumber(cells[0]["x"]), na.AsNumber(cells[0]["z"]), na.AsNumber(cells[0]["x"]), na.AsNumber(cells[0]["z"])
	for _, cell := range cells {
		x, z := na.AsNumber(cell["x"]), na.AsNumber(cell["z"])
		fMinX, fMinZ, fMaxX, fMaxZ = min(fMinX, x), min(fMinZ, z), max(fMaxX, x), max(fMaxZ, z)
	}
	if minX != fMinX || minZ != fMinZ || maxX != fMaxX || maxZ != fMaxZ {
		return fmt.Errorf("observation_invalidated rectangle (%v,%v)-(%v,%v) is not the zone footprint (%v,%v)-(%v,%v)", minX, minZ, maxX, maxZ, fMinX, fMinZ, fMaxX, fMaxZ)
	}
	return nil
}

// asStrings projects a decoded JSON array of strings.
func asStrings(v any) []string {
	var out []string
	for _, item := range na.AsSlice(v) {
		out = append(out, na.AsString(item))
	}
	return out
}
