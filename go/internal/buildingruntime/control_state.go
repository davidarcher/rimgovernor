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

// TargetsWorld reports whether the control's private cleanup target is world:
// the world this process last acquired or observed, kept while observation is
// unknown. A status read that failed or was cancelled leaves the process's own
// Auto grant standing natively; the player's next Resume or Pause revokes it
// through this target before acquiring again (#328). The target itself stays
// private: this authorizes nothing but that cleanup.
func (control *Control) TargetsWorld(world store.World) bool {
	if control == nil {
		return false
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	return !control.closing && !control.closed && control.haveTarget && playerWorld(control.snapshot) == world
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
