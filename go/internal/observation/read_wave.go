package observation

import (
	"context"
	"sync"
)

// readWave runs independent native reads at once and reports the first
// failure. Unlike a planner wave (buildingruntime.plannerGroup) a failed
// read is not isolated: one stale or refused reply invalidates the whole
// bracket, so the first error cancels the lanes still in flight and Wait
// returns it. Each lane writes only its own fields; Wait's return
// happens-after every lane, so the caller reads them without locking.
type readWave struct {
	ctx    context.Context
	cancel context.CancelFunc
	wg     sync.WaitGroup
	once   sync.Once
	err    error
}

func newReadWave(ctx context.Context) *readWave {
	ctx, cancel := context.WithCancel(ctx)
	return &readWave{ctx: ctx, cancel: cancel}
}

func (w *readWave) Go(read func(context.Context) error) {
	w.wg.Add(1)
	go func() {
		defer w.wg.Done()
		if err := read(w.ctx); err != nil {
			w.once.Do(func() { w.err = err; w.cancel() })
		}
	}()
}

// Wait blocks for every lane and returns the first error; a caller context
// that ends the wave surfaces as the first lane's context error.
func (w *readWave) Wait() error {
	w.wg.Wait()
	w.cancel()
	return w.err
}
