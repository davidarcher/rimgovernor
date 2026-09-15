package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// AttackAttempt retains the original guarded target and admission. Only
// explicit melee or ranged modes are supported (see attackJobDef); auto-mode
// selection and explosive-verb attribution stay outside this contract.
type AttackAttempt struct {
	Identity                                             *c.Identity
	Attempt                                              *c.AttemptKey
	NativeGeneration                                     uint64
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
