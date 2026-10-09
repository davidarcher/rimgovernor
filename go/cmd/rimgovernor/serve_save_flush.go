package main

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

const (
	// saveFlushBudget is how long a pre_save flush may run: native parks the
	// vanilla save for about 3 s, so a flush past it only lands in the next save.
	saveFlushBudget = 3 * time.Second
	// saveAckTimeout bounds the flush_done call.
	saveAckTimeout = 5 * time.Second
	// saveSignalRetry is the pause after a failed wait (disconnected, reconnecting).
	saveSignalRetry = time.Second
)

var errNoGovernorWorld = errors.New("no current world to flush governor state into")

// stateFlusher writes the store's governor state into the save's component:
// every blob in one batched put, no diffing. The world is rebuilt
// from the save first when it is new, so a flush never replaces the save's
// blobs with a store that has not read them yet.
type stateFlusher struct {
	native   governorStateNative
	world    func(context.Context) (governorWorld, bool)
	database *store.Store
	rebuild  *worldRebuild
}

func (f *stateFlusher) Flush(ctx context.Context) error {
	world, ok := f.world(ctx)
	if !ok {
		return errNoGovernorWorld
	}
	if err := f.rebuild.ensure(ctx, world, f.native); err != nil {
		return err
	}
	blobs, err := f.database.GovernorStateBlobs(ctx)
	if err != nil {
		return err
	}
	return f.native.PutGovernorStateBatch(ctx, blobs)
}

// flushedSave is the lifecycle save capability every Go-made save goes
// through: it flushes the governor state first, so the file carries fresh
// blobs. A failed flush refuses the save.
type flushedSave struct {
	*bridge.LifecycleSave
	flush func(context.Context) error
}

func (s *flushedSave) Save(ctx context.Context, request *l.SaveRequest) (*l.SaveReply, bridge.Result, error) {
	if s.flush != nil {
		if err := s.flush(ctx); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	return s.LifecycleSave.Save(ctx, request)
}

// saveSignalNative is the pre_save long-poll and its ack.
type saveSignalNative interface {
	WaitSaveSignal(ctx context.Context, timeout time.Duration) (string, bool, error)
	FlushDone(ctx context.Context, token string) error
}

// runSaveFlusher holds the one pre_save wait: on a signal it flushes within
// the budget, then acks whether or not the flush failed (native must not
// wait out its timeout). It re-arms after every return, a quiet timeout and a
// transport error alike, so a reconnect picks the wait up again.
func runSaveFlusher(ctx context.Context, native saveSignalNative, flush func(context.Context) error, retry time.Duration) {
	log := slog.With(telemetry.ComponentKey, "save-flush")
	for ctx.Err() == nil {
		token, ok, err := native.WaitSaveSignal(ctx, 0)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			log.WarnContext(ctx, "save signal wait failed", "error", err)
			select {
			case <-ctx.Done():
				return
			case <-time.After(retry):
			}
			continue
		}
		if !ok {
			continue
		}
		began := time.Now()
		flushCtx, cancel := context.WithTimeout(ctx, saveFlushBudget)
		flushErr := flush(flushCtx)
		cancel()
		flushed := time.Since(began)
		ackCtx, ackCancel := context.WithTimeout(context.WithoutCancel(ctx), saveAckTimeout)
		ackErr := native.FlushDone(ackCtx, token)
		ackCancel()
		if flushErr != nil {
			log.WarnContext(ctx, "pre_save flush failed; acked anyway", "error", flushErr, "flush", flushed.Round(time.Millisecond))
		} else {
			log.InfoContext(ctx, "pre_save flushed", "flush", flushed.Round(time.Millisecond), "total", time.Since(began).Round(time.Millisecond))
		}
		if ackErr != nil {
			log.WarnContext(ctx, "pre_save ack failed", "error", ackErr, "stale", errors.Is(ackErr, bridge.ErrStaleSaveToken))
		}
	}
}
