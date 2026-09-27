package boundary

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// DispatchWrite runs the write half shared by every direct-write boundary
// (Bill, Work, Zone, Supply, Acquisition): validate, lease, issue the single
// write call, and classify the resulting receipt. validate does the
// domain-specific action extraction and any pre-write token check (Bill and
// Work reject a stale SnapshotToken here; Zone/Supply/Acquisition don't, since
// their target's token travels inside the write call itself and is checked
// natively). write receives the built precondition and performs the actual
// writer call.
func (b *Boundary) DispatchWrite(ctx context.Context, p executor.Placement, validate func() error, write func(*a.WritePrecondition) (*o.ExecuteReply, bridge.Result, error)) (executor.Receipt, error) {
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	if err := validate(); err != nil {
		return out, err
	}
	lease, err := b.Leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	pre := &a.WritePrecondition{Identity: Identity(p.Snapshot), Attempt: b.Attempt(p), ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native))}
	reply, _, err := write(pre)
	if err != nil {
		return out, err
	} // Refusals remain uncertain until correlated inspection.
	if err = Admission(reply.GetReceipt(), p, b.Session); err != nil {
		return out, err
	}
	if reply.GetReceipt().GetApplied() != nil {
		out.Kind = domain.ReceiptAccepted
	}
	return out, nil
}
