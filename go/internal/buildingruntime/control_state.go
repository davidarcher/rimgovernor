package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ControlState exposes current scope and local permission, never lease secrets.
// Unknown observation scope has a zero Snapshot. It cannot authorize inspection.
type ControlState struct {
	Snapshot         domain.GenerationSnapshot
	ObservationKnown bool
	Enabled          bool
}

// State checks expiry synchronously. Closed controls expose no current authority.
func (control *Control) State() ControlState {
	if control == nil {
		return ControlState{}
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	return control.stateLocked()
}

func (control *Control) stateLocked() ControlState {
	if control.closing || control.closed {
		return ControlState{}
	}
	state := ControlState{Enabled: control.liveLocked(), ObservationKnown: control.observationKnown}
	if state.ObservationKnown {
		state.Snapshot = control.snapshot
	}
	return state
}

// A read from before acquisition cannot revoke the replacement permission.
func (control *Control) disableObserved(expected ControlState) error {
	control.mu.Lock()
	defer control.mu.Unlock()
	if control.stateLocked() != expected {
		return store.ErrConflict
	}
	if control.closing || control.closed {
		return ErrControl
	}
	return control.invalidateLocked()
}

// Disable synchronously invalidates local writes before the player coordinator
// persists control intent. It performs no native operation and preserves known
// read context. Native cleanup remains the responsibility of Manual and Close.
func (control *Control) Disable() error {
	if control == nil {
		return ErrControl
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	if control.closing || control.closed {
		return ErrControl
	}
	return control.invalidateLocked()
}
