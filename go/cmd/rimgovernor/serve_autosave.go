package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

const (
	// autosaveIntervalTicks is the game time between Go saves: one in-game
	// day, the same span as a routine clock window.
	autosaveIntervalTicks = 60000
	// autosaveSlots is how many rotating Autosave-N files Go keeps.
	autosaveSlots = 5
)

// goAutosaver is the Go-owned autosave, run in the clock scheduler's
// stop between windows now that native skips vanilla autosave. The last-save
// tick and the slot are runtime state, not persisted: the first stop of a
// process or of a rewound tick (a load) only sets the baseline.
type goAutosaver struct {
	save     func(context.Context, *l.SaveRequest) (*l.SaveReply, bridge.Result, error)
	interval int64
	mu       sync.Mutex
	known    bool
	last     int64
	slot     int
}

func newGoAutosaver(save func(context.Context, *l.SaveRequest) (*l.SaveReply, bridge.Result, error)) *goAutosaver {
	return &goAutosaver{save: save, interval: autosaveIntervalTicks}
}

// AtStop saves when the interval has elapsed since the last Go save. A failed
// or uncertain save is logged and leaves the mark, so the next stop (a fresh
// observation) tries again; only a completed save advances the mark and slot.
func (a *goAutosaver) AtStop(ctx context.Context, identity *c.Identity, tick int64) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if !a.known || tick < a.last {
		a.known, a.last = true, tick
		return
	}
	if tick-a.last < a.interval {
		return
	}
	name := fmt.Sprintf("Autosave-%d", a.slot%autosaveSlots+1)
	request := &l.SaveRequest{
		Player: &l.PlayerLifecycleContext{Identity: identity, PlayerDirection: proto.Uint64(bridge.LifecycleDirection),
			RequestId: proto.String(fmt.Sprintf("autosave-%d", time.Now().UnixNano()))},
		SaveName: proto.String(name),
	}
	log := slog.With(telemetry.ComponentKey, "autosave")
	if _, _, err := a.save(ctx, request); err != nil {
		log.WarnContext(ctx, "autosave failed; retrying at the next stop", "save", name, "tick", tick, "uncertain", errors.Is(err, bridge.ErrSaveUncertain), "error", err)
		return
	}
	a.last = tick
	a.slot++
	log.InfoContext(ctx, "autosaved", "save", name, "tick", tick)
}
