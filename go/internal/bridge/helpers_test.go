package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// snapshot reports the slots held and the calls waiting per class.
func (a *admission) snapshot() (held, waiting map[AdmissionClass]int) {
	a.mu.Lock()
	defer a.mu.Unlock()
	held = map[AdmissionClass]int{}
	for class, n := range a.held {
		held[class] = n
	}
	waiting = map[AdmissionClass]int{}
	for _, w := range a.waiting {
		waiting[w.class]++
	}
	return held, waiting
}

// ValidateClockEvent validates retained typed evidence without acknowledging it
// or granting permission to resume. It does not require the event's original
// world or epoch to be current.
func ValidateClockEvent(event *k.Event) error {
	if err := clockWire(event); err != nil {
		return err
	}
	return clockEvent(event)
}

// ValidateCombatPawnSnapshot shares exact-ID validation with ReadCombatPawns.
func ValidateCombatPawnSnapshot(snapshot *o.PawnSnapshot, identity *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, identity, ids, pawnDetails{Combat: true})
}

// ValidateTendPawnSnapshot shares exact-ID validation with ReadTendPawns.
func ValidateTendPawnSnapshot(snapshot *o.PawnSnapshot, identity *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, identity, ids, pawnDetails{Combat: true, Work: true, Care: true, Tend: true})
}

func pawnsSnapshot(v *o.PawnSnapshot, id *c.Identity, requested map[string]bool) error {
	return pawnsSnapshotDetails(v, id, requested, false)
}

func pawnsSnapshotDetails(v *o.PawnSnapshot, id *c.Identity, requested map[string]bool, combat bool) error {
	return pawnsSnapshotSelected(v, id, requested, pawnDetails{Combat: combat})
}

func decodeAllowSupplies(reply *o.ListSuppliesReply, identity *c.Identity, cell domain.Cell) (SupplyRead, error) {
	return decodeSupplyAccess(reply, identity, cell, false)
}
