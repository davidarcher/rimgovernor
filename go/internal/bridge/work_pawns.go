package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadRoutinePawns also requests schedule (TimetableSlot) detail: the shared
// routine census (ObserveRoutine/routineBracket) feeds every routine
// planner -- Work, Waste, Disaster, Mood and others -- from this one read, and
// EnsureMood-* relief dispatch needs a pawn's current timetable assignment
// (boundary.ExpectedScheduleDef) to fence its native writes. Requesting it
// here, once, for the whole shared census matches how Needs is already
// requested unconditionally for the same reason rather than per-consumer.
func (client *Client) ReadRoutinePawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, id, ids, true, true, false, true)
}
func ValidateRoutinePawnSnapshot(snapshot *o.PawnSnapshot, id *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, id, ids, true, false, true)
}

// validateSettings enforces that PawnSettings carries only the fields the
// request actually asked for: work priorities and the allowed area under
// work (the work snapshot token commits to both, and PatchPawn writes both --
// WorkBoundary's area readback depends on it, #167), care policy under care,
// timetable slots under schedule. Any subset may be set; unrequested fields
// are refused.
func validateSettings(s *o.PawnSettings, work, care, schedule bool) error {
	allowed := &o.PawnSettings{Snapshot: s.Snapshot, Issues: s.Issues}
	if work {
		allowed.Work, allowed.WorkApplies, allowed.ManualWorkPriorities, allowed.AllowedAreaId = s.Work, s.WorkApplies, s.ManualWorkPriorities, s.AllowedAreaId
	}
	if care {
		allowed.MedicalCare, allowed.SelfTend = s.MedicalCare, s.SelfTend
	}
	if schedule {
		allowed.Schedule = s.Schedule
	}
	if !proto.Equal(s, allowed) || len(s.Work) > 256 || len(s.Schedule) > 24 {
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
	if work && s.AllowedAreaId != nil {
		if err := validID(s.GetAllowedAreaId()); err != nil {
			return err
		}
	}
	if care && s.MedicalCare != nil {
		if err := validID(s.GetMedicalCare()); err != nil {
			return err
		}
	}
	if schedule {
		seenHours := map[uint32]bool{}
		for _, slot := range s.Schedule {
			if slot == nil || slot.Hour == nil || slot.GetHour() > 23 || seenHours[slot.GetHour()] {
				return contract("invalid or duplicate timetable slot")
			}
			seenHours[slot.GetHour()] = true
			if slot.AssignmentDefName != nil {
				if err := validID(slot.GetAssignmentDefName()); err != nil {
					return err
				}
			}
		}
	}
	return nil
}
