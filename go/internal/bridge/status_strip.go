package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// DrawStatusStrip replaces every row of the in-game status strip (#823);
// enabled=false hides it. Output only, never read back or persisted.
func (client *Client) DrawStatusStrip(ctx context.Context, identity *c.Identity, rows []policy.StatusRow, enabled bool) (*p.StatusStripApplied, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &p.StatusStripRequest{Identity: proto.Clone(identity).(*c.Identity), Enabled: proto.Bool(enabled)}
	if enabled {
		request.Rows = statusStripRows(rows)
	}
	reply := &p.StatusStripReply{}
	raw, err := client.protoCall(ctx, "rimgovernor/presentation_status_strip", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.StatusStripReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *p.StatusStripReply_Applied:
		if v.Applied.GetContext() == nil {
			return nil, raw, contract("status strip context required")
		}
		return v.Applied, raw, nil
	}
	return nil, raw, contract("status strip outcome required")
}

func statusStripRows(rows []policy.StatusRow) []*p.StatusRow {
	out := make([]*p.StatusRow, 0, len(rows))
	for _, r := range rows {
		row := &p.StatusRow{Key: proto.String(r.Key), Text: proto.String(r.Text), Severity: p.StatusSeverity_STATUS_SEVERITY_INFO}
		switch r.Severity {
		case policy.StatusWarning:
			row.Severity = p.StatusSeverity_STATUS_SEVERITY_WARNING
		case policy.StatusCritical:
			row.Severity = p.StatusSeverity_STATUS_SEVERITY_CRITICAL
		}
		if cell, ok := r.Target.Value(); ok {
			row.Target = &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
		}
		if r.Detail {
			row.Detail = proto.Bool(true)
		}
		out = append(out, row)
	}
	return out
}
