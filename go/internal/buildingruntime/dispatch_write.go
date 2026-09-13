package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// dispatchWrite runs the write half shared by every direct-write boundary
// (Bill, Work, Zone, Supply, Acquisition): validate, lease, issue the single
// write call, and classify the resulting receipt. validate does the
// domain-specific action extraction and any pre-write token check (Bill and
// Work reject a stale SnapshotToken here; Zone/Supply/Acquisition don't, since
// their target's token travels inside the write call itself and is checked
// natively). write receives the built precondition and performs the actual
// writer call.
func (b *Boundary) dispatchWrite(ctx context.Context, p executor.Placement, validate func() error, write func(*a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error)) (executor.Receipt, error) {
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	if err := validate(); err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: boundaryIdentity(p.Snapshot), Attempt: b.attempt(p), LeaseId: proto.String(lease), ExpectedGeneration: proto.Uint64(uint64(p.Snapshot.Native))}
	reply, _, err := write(pre)
	if err != nil {
		return out, err
	} // Refusals remain uncertain until correlated inspection.
	if err = boundaryAdmission(reply.GetReceipt(), p, b.session); err != nil {
		return out, err
	}
	if reply.GetReceipt().GetApplied() != nil {
		out.Kind = domain.ReceiptAccepted
	}
	return out, nil
}
