package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type EquipJournal interface {
	Journal
	PrepareEquip(context.Context, domain.PlanID, domain.ActionID, store.EquipAdmission) (domain.Progress, error)
}

type EquipInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.EquipFacts
}

type EquipDispatch struct {
	Attempt   Placement
	Admission store.EquipAdmission
}

type EquipEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
	Thing                 string
}

// EquipBoundary is optionally composed, like HaulBoundary: the pawn is not
// drafted, and the planner has already selected the pawn/weapon pair, so this
// family attaches without a hard NewWithEquip ctor.
type EquipBoundary interface {
	InspectEquip(context.Context, Target) (EquipInspection, error)
	EquipPawn(context.Context, EquipDispatch) (Receipt, error)
	ObserveEquip(context.Context, EquipDispatch, domain.GenerationSnapshot) (EquipEvidence, error)
}

// EnableEquip activates the equip capability; see EnableAcquisition (in
// acquisition.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableEquip(equip EquipBoundary) error {
	if equip == nil {
		return errors.New("equip boundary required")
	}
	j, ok := e.journal.(EquipJournal)
	if !ok {
		return errors.New("equip boundary requires typed journal")
	}
	e.equip, e.equipJournal = equip, j
	return nil
}
