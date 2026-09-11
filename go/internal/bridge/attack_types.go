package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// AttackAttempt retains the original guarded target and admission. Only explicit
// melee is supported until ranged scope has a canonical native restriction.
type AttackAttempt struct {
	Identity                                             *c.Identity
	Attempt                                              *c.AttemptKey
	NativeGeneration                                     uint64
	Owner                                                *a.Owner
	PawnID, TargetID                                     string
	Mode                                                 o.AttackMode
	RequireHostile, RequireStanding, RequireCombatHealth bool
}

type AttackControl struct{ client *Client }

func NewAttackControl(client *Client) (*AttackControl, error) {
	if client == nil {
		return nil, contract("attack client missing")
	}
	return &AttackControl{client: client}, nil
}
