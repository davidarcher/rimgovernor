package buildingruntime

import (
	"context"
	"sync"
)

type clockWorkerStopper interface{ Stop(context.Context) error }

type clockWorkerSlot struct {
	mu      sync.Mutex
	worker  clockWorkerStopper
	closing bool
}

func (s *Session) attachClockWorker(worker clockWorkerStopper) error {
	if s == nil || s.control == nil || s.clock == nil || s.clockWorkers == nil || worker == nil {
		return ErrControl
	}
	// Control invalidation uses this same lock before closing can reach StopWrites.
	s.control.mu.Lock()
	defer s.control.mu.Unlock()
	if s.control.closing || s.control.closed {
		return ErrControl
	}
	slot := s.clockWorkers
	slot.mu.Lock()
	defer slot.mu.Unlock()
	if slot.closing || slot.worker != nil {
		return ErrControl
	}
	slot.worker = worker
	return nil
}

func (slot *clockWorkerSlot) stop(ctx context.Context) (bool, error) {
	if slot == nil {
		return false, nil
	}
	slot.mu.Lock()
	slot.closing = true
	worker := slot.worker
	slot.mu.Unlock()
	if worker == nil {
		return false, nil
	}
	// Joining may need the coordinator and journal; never hold lifecycle locks.
	return true, worker.Stop(ctx)
}

func (s *Session) disableClockWorker() error {
	err := s.Disable()
	if err == nil {
		return nil
	}
	s.control.mu.Lock()
	alreadyDisabled := s.control.closing || s.control.closed
	s.control.mu.Unlock()
	if alreadyDisabled {
		return nil
	}
	return err
}
