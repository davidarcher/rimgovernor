package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// DraftAttempt correlates an immutable admitted operation without retaining a
// lease. It never authorizes a draft or substitutes for fresh pawn ownership.
type DraftAttempt struct {
	Identity         *c.Identity
	Attempt          *c.AttemptKey
	NativeGeneration uint64
	Owner            *a.Owner
	PawnID           string
}

// DraftControl grants only temporary drafting. The runtime owns live authority.
type DraftControl struct{ client *Client }

// DraftCleanup can release only the exact original native claim, even after
// authority expires. It cannot draft a pawn or acquire a lease.
type DraftCleanup struct{ client *Client }

func NewDraftControl(client *Client) (*DraftControl, error) {
	if client == nil {
		return nil, contract("draft client missing")
	}
	return &DraftControl{client}, nil
}
func NewDraftCleanup(client *Client) (*DraftCleanup, error) {
	if client == nil {
		return nil, contract("draft cleanup client missing")
	}
	return &DraftCleanup{client}, nil
}
