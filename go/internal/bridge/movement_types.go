package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// MovementAttempt correlates one exact pawn and destination with its original
// admission. Ownership of the prerequisite draft remains a separate obligation.
type MovementAttempt struct {
	Identity         *c.Identity
	Attempt          *c.AttemptKey
	NativeGeneration uint64
	Owner            *a.Owner
	PawnID           string
	Destination      *c.Cell
}

// MovementControl can issue ordinary owned movement, never draft or adopt a pawn.
type MovementControl struct{ client *Client }

func NewMovementControl(client *Client) (*MovementControl, error) {
	if client == nil {
		return nil, contract("movement client missing")
	}
	return &MovementControl{client: client}, nil
}
