package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// previewZone runs a zone preview and separates the game refusing the site
// (a cell the zone designator will not take, a snapshot that moved) from a
// failed read. A refusal is a site outcome the planner moves on from, as
// the building planners do with an unplaceable preview; every other error
// still fails the step. The native detail names the refusing condition.
func previewZone(ctx context.Context, native FieldNative, identity *c.Identity, target bridge.ZoneTarget) (*op.PreviewReply, string, error) {
	reply, _, err := native.PreviewZone(ctx, identity, target)
	var refusal *bridge.NativeFailure
	if errors.As(err, &refusal) {
		detail := refusal.Value.GetDetail()
		if detail == "" {
			detail = refusal.Value.GetCode().String()
		}
		return nil, detail, nil
	}
	if err != nil {
		return nil, "", err
	}
	if v := reply.GetEvaluated(); v == nil || !v.GetAccepted() {
		return nil, "preview not accepted", nil
	}
	return reply, "", nil
}
