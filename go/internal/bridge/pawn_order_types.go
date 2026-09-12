package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
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
	Owner              *a.Owner
	PawnID, TargetID   string
	Kind               o.PawnOrderKind
	RequireSafeStorage bool
}

type PawnOrderControl struct{ client *Client }

func NewPawnOrderControl(client *Client) (*PawnOrderControl, error) {
	if client == nil {
		return nil, contract("pawn order capability missing")
	}
	return &PawnOrderControl{client: client}, nil
}

// pawnOrderJobDef is the only native job def a given kind may report. Both
// tend and rescue are undrafted vanilla jobs; explosives and drafted combat
// stay on the AttackTarget contract.
func pawnOrderJobDef(kind o.PawnOrderKind) string {
	switch kind {
	case o.PawnOrderKind_PAWN_ORDER_KIND_TEND:
		return "TendPatient"
	case o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE:
		return "Rescue"
	default:
		return ""
	}
}
