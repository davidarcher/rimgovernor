package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func (client *Client) ReadRoutinePawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, id, ids, true, true)
}
func ValidateRoutinePawnSnapshot(snapshot *o.PawnSnapshot, id *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, id, ids, true)
}
func validateWorkSettings(s *o.PawnSettings) error {
	if !proto.Equal(s, &o.PawnSettings{Work: s.Work, WorkApplies: s.WorkApplies, ManualWorkPriorities: s.ManualWorkPriorities, Issues: s.Issues}) || len(s.Work) > 256 {
		return contract("unrequested work settings detail")
	}
	if err := pawnsIssues(s.Issues, s.ProtoReflect()); err != nil {
		return err
	}
	if s.WorkApplies != nil && !s.GetWorkApplies() && len(s.Work) > 0 {
		return contract("inapplicable work contains priorities")
	}
	seen := map[string]bool{}
	for _, w := range s.Work {
		if w == nil || validID(w.GetDefName()) != nil || seen[w.GetDefName()] || w.Priority != nil && (w.GetPriority() < 0 || w.GetPriority() > 4) {
			return contract("invalid or duplicate work priority")
		}
		seen[w.GetDefName()] = true
		if s.ManualWorkPriorities != nil && !s.GetManualWorkPriorities() && w.Priority != nil && w.GetPriority() != 0 && w.GetPriority() != 3 {
			return contract("invalid effective checkbox priority")
		}
	}
	return nil
}
