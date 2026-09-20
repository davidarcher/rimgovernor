package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// PawnOrderAttempt retains the original guarded target and admission for one
// undrafted pawn-target order (tend, rescue). Drafted job families (melee,
// ranged attack) use AttackAttempt instead.
type PawnOrderAttempt struct {
	Identity           *c.Identity
	Attempt            *c.AttemptKey
	NativeGeneration   uint64
	PawnID, TargetID   string
	Kind               o.PawnOrderKind
	RequireSafeStorage bool
	// ArrestBed selects the existing Arrest operation for custody of a standing ancient.
	ArrestBed string
}

type PawnOrderControl struct{ client *Client }

func NewPawnOrderControl(client *Client) (*PawnOrderControl, error) {
	if client == nil {
		return nil, contract("pawn order capability missing")
	}
	return &PawnOrderControl{client: client}, nil
}

// pawnOrderJobDefs lists the native job defs a given kind may report. Tend,
// rescue and capture are single fixed undrafted vanilla jobs; haul's storage
// search may produce either a cell or container destination job; equip is a
// single fixed job available to a drafted or undrafted pawn
// (FloatMenuOptionProvider_Equip applies no draft gate); repair is
// WorkGiver_Repair's single fixed job (issue #2); open casket is
// JobDriver_Open's single Open job, ordered to a drafted or undrafted pawn
// (#460). Explosives and drafted combat stay on the AttackTarget contract.
func pawnOrderJobDefs(kind o.PawnOrderKind) []string {
	switch kind {
	case o.PawnOrderKind_PAWN_ORDER_KIND_TEND:
		return []string{"TendPatient"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE:
		return []string{"Rescue"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE:
		return []string{"Capture"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_HAUL:
		return []string{"HaulToCell", "HaulToContainer"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_EQUIP:
		return []string{"Equip"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN:
		return []string{"Clean"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR:
		return []string{"Repair"}
	case o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET:
		return []string{"Open"}
	default:
		return nil
	}
}
func pawnOrderJobDefAllowed(kind o.PawnOrderKind, jobDef string) bool {
	for _, allowed := range pawnOrderJobDefs(kind) {
		if allowed == jobDef {
			return true
		}
	}
	return false
}

// pawnOrderRequiresSafeStorage is true only for haul, whose exact-quantity
// ledger the native side gates on this flag; tend/rescue carry no ledger.
func pawnOrderRequiresSafeStorage(kind o.PawnOrderKind) bool {
	return kind == o.PawnOrderKind_PAWN_ORDER_KIND_HAUL
}
