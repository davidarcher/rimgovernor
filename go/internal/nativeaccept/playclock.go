package nativeaccept

import (
	"context"
	"fmt"
)

// SupervisedPlayClock is the bounded supervised clock cmd/tickbudgetaccept
// drives: Change("Paused"), Change(speed, maxTicks) for the ordinary colony-mode
// start/replace flow, and Poll's heartbeat/event-cursor bookkeeping. It
// intentionally omits durable cursor/epoch persistence, dialog pausing, combat
// mode, ignored-hostile/downed IDs, surgical/medical-rest monitoring, test
// acceleration, and any owner/epoch-mismatch race recovery inside the Paused
// case (Paused calls always target a clock this instance either just started
// or never started, so that race is not reachable here). A future caller
// needing any of these should extend this type rather than start over.
type SupervisedPlayClock struct {
	Owner string
	Hold  string
	State map[string]any

	epoch    int64
	cursor   int64
	ackSet   bool
	ackEpoch any
	ackStop  string
}

// NewSupervisedPlayClock creates a fresh clock with a random owner, mirroring
// PlayClock.__init__(bridge) with no store/context.
func NewSupervisedPlayClock(owner string) *SupervisedPlayClock {
	return &SupervisedPlayClock{Owner: owner}
}

func (c *SupervisedPlayClock) call(ctx context.Context, h *Harness, label string, arguments map[string]any) (map[string]any, error) {
	result, err := h.Call(ctx, label, "home/supervised_play", arguments)
	if err != nil {
		return nil, err
	}
	if success, ok := AsBool(result["success"]); !ok || !success {
		return nil, fmt.Errorf("native clock did not confirm the request: %#v", result)
	}
	return result, nil
}

// absorb mirrors PlayClock.absorb(): a stop reason this clock owns and hasn't already
// acknowledged (via AllowResume) becomes a recorded external hold.
func (c *SupervisedPlayClock) absorb(state map[string]any) {
	c.State = state
	owner := AsString(state["owner"])
	reason := AsString(state["stopReason"])
	acknowledged := c.ackSet && DeepEqual(c.ackEpoch, state["epoch"]) && c.ackStop == reason
	if owner == c.Owner && scenarioHoldReasons[reason] && !acknowledged {
		c.Hold = reason
	}
}

// AllowResume mirrors PlayClock.allow_resume(): acknowledge the current stop so a
// later Change no longer treats it as an unresolved hold.
func (c *SupervisedPlayClock) AllowResume() {
	c.ackEpoch, c.ackStop, c.ackSet = c.State["epoch"], AsString(c.State["stopReason"]), true
	c.Hold = ""
}

// Change mirrors the reachable subset of PlayClock.change(): speed is one of
// "Paused", "Normal", "Fast", "Superfast"; maxTicks is nil for Paused (matching
// max_ticks=None) or a 1..1800000 native tick budget otherwise.
func (c *SupervisedPlayClock) Change(ctx context.Context, h *Harness, label, speed string, maxTicks *uint64) (map[string]any, error) {
	switch speed {
	case "Paused", "Normal", "Fast", "Superfast":
	default:
		return nil, fmt.Errorf("choose Paused, Normal, Fast or Superfast")
	}
	if maxTicks != nil && (*maxTicks < 1 || *maxTicks > 1800000) {
		return nil, fmt.Errorf("native execution budget must be 1..1800000 game ticks")
	}
	state, err := c.call(ctx, h, label+"-status", map[string]any{"op": "status"})
	if err != nil {
		return nil, err
	}
	c.absorb(state)
	if speed != "Paused" && c.Hold != "" {
		return nil, fmt.Errorf("clock held: %s. the player must enable Automate again to resume", c.Hold)
	}
	active, _ := AsBool(state["active"])
	if active && AsString(state["owner"]) != c.Owner {
		return nil, fmt.Errorf("another controller owns the native clock; waiting for its lease to expire")
	}
	if speed != "Paused" && maxTicks != nil {
		if boundary, _ := AsBool(state["nativeTickBoundary"]); !boundary {
			return nil, fmt.Errorf("installed native clock lacks tick boundaries; update the observation companion before automatic execution")
		}
	}
	if speed == "Paused" {
		if active {
			result, err := c.call(ctx, h, label+"-pause", map[string]any{"op": "pause", "owner": c.Owner, "epoch": state["epoch"]})
			if err != nil {
				return nil, err
			}
			c.absorb(result)
			return result, nil
		}
		if _, err := h.Call(ctx, label+"-settime", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return nil, err
		}
		status, err := h.Call(ctx, label+"-homestatus", "home/status", map[string]any{"colonists": false, "threats": false})
		if err != nil {
			return nil, err
		}
		timeStatus, _ := AsMap(status["time"])
		if paused, _ := AsBool(timeStatus["paused"]); !paused {
			return nil, fmt.Errorf("native game pause was not confirmed")
		}
		result := Merge(state, map[string]any{"paused": true, "pauseVerified": true})
		c.absorb(result)
		return result, nil
	}
	if active {
		if _, err := c.call(ctx, h, label+"-pause-replace", map[string]any{"op": "pause", "owner": c.Owner, "epoch": state["epoch"]}); err != nil {
			return nil, err
		}
	}
	if c.epoch == 0 {
		c.cursor = int64(AsNumber(state["newestCursor"]))
	}
	args := map[string]any{
		"op": "start", "owner": c.Owner, "leaseMs": 15000, "speed": speed, "mode": "colony",
		"hostileWithin": 40, "ignoredHostileIds": "", "ignoredDownedColonistIds": "", "injuryStopCooldownMs": 0,
	}
	if maxTicks != nil {
		args["maxTicks"] = *maxTicks
	}
	result, err := c.call(ctx, h, label+"-start", args)
	if err != nil {
		return nil, err
	}
	c.epoch = int64(AsNumber(result["epoch"]))
	c.absorb(result)
	return result, nil
}

// Poll mirrors PlayClock.poll(): heartbeat an owned running clock, then drain events
// newer than the last poll for the current epoch.
func (c *SupervisedPlayClock) Poll(ctx context.Context, h *Harness, label string) ([]any, error) {
	state, err := c.call(ctx, h, label+"-status", map[string]any{"op": "status"})
	if err != nil {
		return nil, err
	}
	c.absorb(state)
	active, _ := AsBool(state["active"])
	if active && AsString(state["owner"]) == c.Owner {
		_, err := c.call(ctx, h, label+"-heartbeat", map[string]any{
			"op": "heartbeat", "owner": c.Owner, "epoch": state["epoch"], "leaseMs": 15000,
		})
		if err != nil {
			latest, latestErr := c.call(ctx, h, label+"-heartbeat-status", map[string]any{"op": "status"})
			if latestErr != nil {
				return nil, err
			}
			c.absorb(latest)
			if latestActive, _ := AsBool(latest["active"]); latestActive {
				return nil, err
			}
		}
	}
	if c.epoch == 0 {
		return nil, nil
	}
	newest := int64(AsNumber(state["newestCursor"]))
	if c.cursor > newest {
		c.Hold = "event_journal_error"
		return nil, fmt.Errorf("native event cursor regressed within the current load")
	}
	batch, err := c.call(ctx, h, label+"-events", map[string]any{"op": "events", "afterCursor": c.cursor, "limit": 128})
	if err != nil {
		return nil, err
	}
	var rows []any
	for _, raw := range AsSlice(batch["events"]) {
		row, ok := AsMap(raw)
		if ok && int64(AsNumber(row["epoch"])) == c.epoch {
			rows = append(rows, row)
		}
	}
	if gap, _ := AsBool(batch["gap"]); gap {
		rows = append([]any{map[string]any{"kind": "event_gap", "detail": "Native event history overflowed; inspect current colony state."}}, rows...)
	}
	c.cursor = int64(AsNumber(batch["nextCursor"]))
	return rows, nil
}
