package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func (client *Client) ReadRoutinePawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, id, ids, true, true, false)
}
func ValidateRoutinePawnSnapshot(snapshot *o.PawnSnapshot, id *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, id, ids, true, false)
}

// validateSettings enforces that PawnSettings carries only the fields the
// request actually asked for: work priorities under work, care policy under
// care. Either, both or neither may be set; unrequested fields are refused.
func validateSettings(s *o.PawnSettings, work, care bool) error {
	allowed := &o.PawnSettings{Snapshot: s.Snapshot, Issues: s.Issues}
	if work {
		allowed.Work, allowed.WorkApplies, allowed.ManualWorkPriorities = s.Work, s.WorkApplies, s.ManualWorkPriorities
	}
	if care {
		allowed.MedicalCare, allowed.SelfTend = s.MedicalCare, s.SelfTend
	}
	if !proto.Equal(s, allowed) || len(s.Work) > 256 {
		return contract("unrequested settings detail")
	}
	if s.Snapshot != nil && (validID(s.Snapshot.GetEntityId()) != nil || validID(s.Snapshot.GetToken()) != nil) {
		return contract("invalid work snapshot")
	}
	if err := pawnsIssues(s.Issues, s.ProtoReflect()); err != nil {
		return err
	}
	if work {
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
	}
	if care && s.MedicalCare != nil {
		if err := validID(s.GetMedicalCare()); err != nil {
			return err
		}
	}
	return nil
}
