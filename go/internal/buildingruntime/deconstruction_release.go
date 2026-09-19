package buildingruntime

import (
	"context"
	"fmt"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// The release key is stable across retries of a revoke. The native ledger
// settles uncertain replies without repeating a release under a new identity.
func releaseDeconstructions(writer DeconstructionWriter, session string) func(context.Context, *c.Identity, uint64) error {
	return func(ctx context.Context, identity *c.Identity, generation uint64) error {
		reply, _, err := writer.ReleaseDeconstructions(ctx, &a.WritePrecondition{
			Identity: identity, ExpectedGeneration: proto.Uint64(generation),
			Attempt: &c.AttemptKey{ControllerSessionId: proto.String(session), ActionId: proto.String(fmt.Sprintf("clearance-release-%d", generation)), AttemptId: proto.Uint64(1)},
		})
		if err != nil {
			return err
		}
		if reply.GetReceipt().GetApplied().GetObserved().GetReleaseDeconstructions() == nil {
			return ErrControl
		}
		return nil
	}
}
