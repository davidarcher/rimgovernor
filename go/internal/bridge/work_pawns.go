package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadRoundsPawns also requests schedule (TimetableSlot) detail: the shared
// routine census (ObserveRounds, from the frame) feeds every routine
// planner -- Work, Waste, Disaster, Mood and others -- from this one read, and
// EnsureMood relief dispatch needs a pawn's current timetable assignment
// (boundary.ExpectedScheduleDef) to fence its native writes. Requesting it
// here, once, for the whole shared census matches how Needs is already
// requested unconditionally for the same reason rather than per-consumer.
// Social (the grouped thought rows) rides the same read: the mood census
// takes each colonist's negative thought pressure from it so MaintainMood
// can defer to the upkeep goal whose facility removes it. A native
// build that skips the block leaves thoughts unknown, never the read failed.
func (client *Client) ReadRoundsPawns(ctx context.Context, id *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, id, ids, pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true})
}
func ValidateRoundsPawnSnapshot(snapshot *o.PawnSnapshot, id *c.Identity, ids []string) error {
	return validateDetailedPawnSnapshot(snapshot, id, ids, pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true})
}

// validateSettings enforces that PawnSettings carries only the fields the
// request actually asked for: work priorities and the allowed area under
// work (the work snapshot token commits to both and WorkSettingsIntent
// writes both), care policy under care,
// timetable slots under schedule. Any subset may be set; unrequested fields
// are refused.
func validateSettings(s *o.PawnSettings, work, care, schedule bool) error {
	allowed := &o.PawnSettings{Issues: s.Issues}
	if work {
		allowed.FoodRestriction = s.FoodRestriction
		allowed.PolicyInputs = s.PolicyInputs
		allowed.Work, allowed.WorkApplies, allowed.ManualWorkPriorities, allowed.AllowedAreaId = s.Work, s.WorkApplies, s.ManualWorkPriorities, s.AllowedAreaId
	}
	if care {
		allowed.MedicalCare, allowed.SelfTend, allowed.HostilityResponse = s.MedicalCare, s.SelfTend, s.HostilityResponse
	}
	if schedule {
		allowed.Schedule = s.Schedule
	}
	if !proto.Equal(s, allowed) || len(s.Schedule) > 24 {
		return contract("unrequested settings detail")
	}
	if err := validatePolicyInputs(s.PolicyInputs); err != nil {
		return err
	}
	if food := s.FoodRestriction; food != nil {
		if validID(food.GetPolicyId()) != nil {
			return contract("invalid food policy identity")
		}
		seen := map[string]bool{}
		for _, def := range food.AllowedDefs {
			if validID(def) != nil || seen[def] {
				return contract("invalid food definition")
			}
			seen[def] = true
		}
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
	if care && s.HostilityResponse != nil && HostilityName(s.GetHostilityResponse()) == "" {
		return contract("invalid hostility response")
	}
	if care && s.MedicalCare != nil {
		if MedicalCareName(s.GetMedicalCare()) == "" {
			return contract("invalid medical care")
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
