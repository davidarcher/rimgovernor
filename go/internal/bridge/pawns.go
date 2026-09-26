package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// ReadPawns observes exact IDs including dead pawns. Missing rows, CAS tokens and
// draft claims never imply death, write permission or controller ownership.
func (client *Client) ReadPawns(ctx context.Context, identity *c.Identity, ids []string) (*o.ListPawnsReply, Result, error) {
	return client.readPawns(ctx, identity, ids, false)
}
func (client *Client) readPawns(ctx context.Context, identity *c.Identity, ids []string, combat bool) (*o.ListPawnsReply, Result, error) {
	return client.readPawnDetails(ctx, identity, ids, pawnDetails{Combat: combat})
}

// pawnDetails selects the PawnDetails families one read asks for. Every family
// is opt-in here, and the reply validator refuses any family the request did
// not select, so a caller sees exactly what it asked for.
type pawnDetails struct{ Combat, Work, Care, Schedule, Social, Tend bool }

func (client *Client) readPawnDetails(ctx context.Context, identity *c.Identity, ids []string, want pawnDetails) (*o.ListPawnsReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(ids) < 1 || len(ids) > 256 {
		return nil, Result{}, contract("pawn IDs outside 1..256")
	}
	requested := make(map[string]bool, len(ids))
	copied := append([]string(nil), ids...)
	for _, id := range copied {
		if err := validID(id); err != nil {
			return nil, Result{}, err
		}
		if requested[id] {
			return nil, Result{}, contract("duplicate requested pawn")
		}
		requested[id] = true
	}
	// Each PawnSettings slice has its own wire flag: Settings gates only native's
	// CarePolicy() (medical_care, self_tend), Work its priorities and Schedule
	// the 24 TimetableSlot rows. Requesting schedule must never also set
	// Settings -- doing so live-fires CarePolicy and returns MedicalCare/SelfTend
	// the caller never asked for, which validateSettings correctly refuses as
	// unrequested detail, permanently failing every routine review (confirmed
	// live: routinehaulaccept/issue #42).
	request := pawnDetailsRequest(identity, copied, want)
	reply := &o.ListPawnsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_pawns", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListPawnsReply_Failure:
		err = failure(v.Failure, raw)
	case *o.ListPawnsReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.ListPawnsReply_Observed:
		err = pawnsSnapshotSelected(v.Observed, request.Scope.ExpectedIdentity, requested, want)
	default:
		err = contract("pawn read outcome missing")
	}
	return reply, raw, err
}

// pawnDetailsRequest is the exact request readPawnDetails issues for ids
// (validated, in the caller's order); the bundle seeds its colonist_pawns
// section under the routine form of it (ReadRoutinePawns).
func pawnDetailsRequest(identity *c.Identity, ids []string, want pawnDetails) *o.ListPawnsRequest {
	request := &o.ListPawnsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.PawnFilter{Ids: append([]string(nil), ids...), IncludeDead: proto.Bool(true)}, Details: &o.PawnDetails{Needs: proto.Bool(false), Health: proto.Bool(want.Combat), Equipment: proto.Bool(want.Combat), Biography: proto.Bool(want.Combat), Settings: proto.Bool(want.Care), Social: proto.Bool(want.Social), Animals: proto.Bool(want.Combat)}, Page: &c.PageRequest{Limit: proto.Uint32(uint32(len(ids)))}}
	if want.Work {
		request.Details.Work = proto.Bool(true)
		request.Details.Needs = proto.Bool(true)
	}
	if want.Schedule {
		request.Details.Schedule = proto.Bool(true)
	}
	if want.Tend {
		request.Details.Tend = proto.Bool(true)
	}
	return request
}

func pawnsSnapshot(v *o.PawnSnapshot, id *c.Identity, requested map[string]bool) error {
	return pawnsSnapshotDetails(v, id, requested, false)
}
func pawnsSnapshotDetails(v *o.PawnSnapshot, id *c.Identity, requested map[string]bool, combat bool) error {
	return pawnsSnapshotSelected(v, id, requested, pawnDetails{Combat: combat})
}
func pawnsSnapshotSelected(v *o.PawnSnapshot, id *c.Identity, requested map[string]bool, want pawnDetails) error {
	combat, work, care, schedule, social := want.Combat, want.Work, want.Care, want.Schedule, want.Social
	if v == nil {
		return contract("pawn snapshot missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, id) {
		return contract("pawn world mismatch")
	}
	counts := v.Completeness
	// ListPawns counts matched query rows; filtered counts excluded source rows.
	// Those excluded rows are not unreadable members of the exact-ID result.
	if len(v.Pawns) > len(requested) || counts == nil || counts.Page == nil || counts.Page.Complete == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Filtered == nil || counts.Unreadable == nil || counts.GetMatched() != uint64(len(v.Pawns)) || counts.GetReturned() != uint64(len(v.Pawns)) || counts.GetUnreadable() != 0 || counts.GetFiltered() > math.MaxUint64-counts.GetReturned() {
		return contract("incomplete pawn query")
	}
	if counts.SnapshotToken != nil {
		if err := validID(counts.GetSnapshotToken()); err != nil {
			return err
		}
	}
	seen := map[string]bool{}
	for _, row := range v.Pawns {
		if row == nil || row.Pawn == nil || !requested[row.Pawn.GetId()] || seen[row.Pawn.GetId()] {
			return contract("unrequested or duplicate pawn")
		}
		seen[row.Pawn.GetId()] = true
		if err := pawnsEntity(row.Pawn, v.Context); err != nil {
			return err
		}
		if !work && row.Needs != nil || !combat && (row.Health != nil || row.Equipment != nil || row.Biography != nil || row.AnimalState != nil) || !work && !care && !schedule && row.Settings != nil || !social && row.Social != nil || !want.Tend && row.TendDoctor != nil {
			return contract("unrequested pawn detail")
		}
		if want.Tend {
			if err := pawnsTendDoctor(row, requested); err != nil {
				return err
			}
		}
		if row.Social != nil {
			if err := pawnsSocial(row.Social); err != nil {
				return err
			}
		}
		if combat {
			if err := combatDetails(row, v.Context); err != nil {
				return err
			}
		}
		if row.Settings != nil {
			if ref := row.Settings.Snapshot; ref != nil && (ref.GetEntityId() != row.Pawn.GetId() || !proto.Equal(ref.Context, v.Context)) {
				return contract("settings snapshot scope mismatch")
			}
			if err := validateSettings(row.Settings, work, care, schedule); err != nil {
				return err
			}
		}
		if work && row.Needs != nil {
			if err := validateMoodNeeds(row.Needs); err != nil {
				return err
			}
		}
		for _, value := range []*string{row.KindDefName, row.FactionId, row.MentalState, row.OwnedBedId, row.LordJobClass, row.LordToilClass} {
			if value != nil {
				if err := validID(*value); err != nil {
					return err
				}
			}
		}
		if _, err := PawnMentalState(row); err != nil {
			return err
		}
		if !presentationText(row.HostileReason, 4096) {
			return contract("invalid pawn hostile reason")
		}
		for _, value := range []*float64{row.ManhunterOnDamageChance, row.NearestColonistDistance} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0) {
				return contract("invalid pawn number")
			}
		}
		if row.ManhunterOnDamageChance != nil && row.GetManhunterOnDamageChance() > 1 {
			return contract("invalid manhunter probability")
		}
		if row.NearestColonist != nil {
			if err := pawnsEntity(row.NearestColonist, v.Context); err != nil {
				return err
			}
		}
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
		if row.Job != nil {
			if err := pawnsJob(row.Job, v.Context); err != nil {
				return err
			}
		}
		if claim := row.DraftClaim; claim != nil {
			switch state := claim.State.(type) {
			case *o.DraftClaimObservation_Unavailable:
				if err := validateUnavailable(state.Unavailable); err != nil {
					return err
				}
			case *o.DraftClaimObservation_Unowned:
				if state.Unowned == nil {
					return contract("empty unowned draft claim")
				}
			case *o.DraftClaimObservation_Owned:
				owned := state.Owned
				if owned == nil {
					return contract("owned draft missing")
				}
				if err := validID(owned.GetClaimId()); err != nil {
					return err
				}
				if owned.PawnSnapshot == nil {
					return contract("owned draft snapshot missing")
				}
				if err := pawnsRef(owned.PawnSnapshot, row.Pawn.GetId(), v.Context); err != nil {
					return err
				}
				if row.Pawn.Snapshot != nil && !proto.Equal(row.Pawn.Snapshot, owned.PawnSnapshot) {
					return contract("draft and pawn snapshots differ")
				}
			default:
				return contract("draft claim state missing")
			}
		}
	}
	return nil
}
func pawnsEntity(v *o.EntityRef, ctx *c.ObservationContext) error {
	if v == nil {
		return contract("pawn entity missing")
	}
	if err := validID(v.GetId()); err != nil {
		return err
	}
	if v.DefName != nil {
		if err := validID(v.GetDefName()); err != nil {
			return err
		}
	}
	if !presentationText(v.Label, 16384) {
		return contract("invalid pawn label")
	}
	if v.MapId != nil && v.GetMapId() != ctx.Identity.GetMapId() {
		return contract("pawn entity map mismatch")
	}
	if v.Position != nil && (v.Position.X == nil || v.Position.Z == nil || v.Position.GetX() < 0 || v.Position.GetZ() < 0) {
		return contract("invalid pawn position")
	}
	if v.Snapshot != nil {
		return pawnsRef(v.Snapshot, v.GetId(), ctx)
	}
	return nil
}
func pawnsRef(v *o.SnapshotRef, id string, ctx *c.ObservationContext) error {
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if v.GetEntityId() != id || !sameIdentity(v.Context.Identity, ctx.Identity) || v.Context.GetTick() > ctx.GetTick() {
		return contract("pawn CAS context or entity mismatch")
	}
	return validID(v.GetToken())
}

// pawnsSocial bounds the thought rows the routine mood census reads: each
// grouped row names a def with finite offsets (one def can appear on several
// rows, since native groups social memories by the other pawn too);
// relations are only bounded here.
func pawnsSocial(v *o.PawnSocial) error {
	if len(v.Relations) > 256 {
		return contract("pawn relations exceed bound")
	}
	for _, rows := range [][]*o.Thought{v.Memories, v.Situational} {
		if len(rows) > 256 {
			return contract("pawn thoughts exceed bound")
		}
		for _, t := range rows {
			if t == nil || validID(t.GetDefName()) != nil || !presentationText(t.Label, 4096) {
				return contract("invalid pawn thought")
			}
			for _, value := range []*float64{t.MoodOffsetEach, t.MoodOffsetTotal} {
				if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0)) {
					return contract("invalid thought offset")
				}
			}
		}
	}
	return pawnsIssues(v.Issues, v.ProtoReflect())
}

// pawnsTendDoctor validates the doctor-side tend gates (#657). Reachability is
// answered pairwise across the rows of this one reply, so every listed ID must
// be another requested pawn, never the row itself. A producer that skips the
// block leaves the gates unknown -- SelectTend then proposes no doctor -- rather
// than failing the read.
func pawnsTendDoctor(row *o.PawnState, requested map[string]bool) error {
	tend := row.TendDoctor
	if tend == nil {
		return nil
	}
	if len(tend.ReachablePawnIds) > len(requested) {
		return contract("too many reachable pawns")
	}
	seen := map[string]bool{}
	for _, id := range tend.ReachablePawnIds {
		if err := validID(id); err != nil {
			return err
		}
		if !requested[id] || id == row.Pawn.GetId() || seen[id] {
			return contract("unrequested, self or duplicate reachable pawn")
		}
		seen[id] = true
	}
	if tend.MissingCapacity != nil {
		if err := validID(tend.GetMissingCapacity()); err != nil {
			return err
		}
		if tend.CapacitiesOk == nil || tend.GetCapacitiesOk() {
			return contract("missing tend capacity contradicts capacities_ok")
		}
	}
	return pawnsIssues(tend.Issues, tend.ProtoReflect())
}

func pawnsIssues(issues []*o.ReadIssue, message protoreflect.Message) error {
	if len(issues) > 256 {
		return contract("too many pawn issues")
	}
	seen := map[string]bool{}
	for _, issue := range issues {
		if issue == nil || validID(issue.GetField()) != nil || seen[issue.GetField()] {
			return contract("invalid or duplicate pawn issue")
		}
		seen[issue.GetField()] = true
		if err := validateUnavailable(issue.Unavailable); err != nil {
			return err
		}
		// Check the exact path only: an unavailable nested field does not erase its
		// parent's other known facts.
		if pawnsFieldPresent(message, issue.GetField()) {
			return contract("%s field %q both known and unavailable", message.Descriptor().Name(), issue.GetField())
		}
	}
	return nil
}
func pawnsFieldPresent(message protoreflect.Message, path string) bool {
	for i, ch := range path {
		if ch == '.' {
			field := message.Descriptor().Fields().ByName(protoreflect.Name(path[:i]))
			if field == nil || field.Kind() != protoreflect.MessageKind || field.IsList() || !message.Has(field) {
				return false
			}
			return pawnsFieldPresent(message.Get(field).Message(), path[i+1:])
		}
	}
	field := message.Descriptor().Fields().ByName(protoreflect.Name(path))
	return field != nil && message.Has(field)
}
func pawnsJob(v *o.JobEvidence, ctx *c.ObservationContext) error {
	for _, id := range []*string{v.DefName, v.LoadId} {
		if id != nil {
			if err := validID(*id); err != nil {
				return err
			}
		}
	}
	if !presentationText(v.Report, 16384) || v.NativePriority != nil && (math.IsNaN(v.GetNativePriority()) || math.IsInf(v.GetNativePriority(), 0)) {
		return contract("invalid pawn job")
	}
	if err := pawnsIssues(v.Issues, v.ProtoReflect()); err != nil {
		return err
	}
	if target := v.TargetA; target != nil {
		switch t := target.Target.(type) {
		case *o.TargetRef_Entity:
			return pawnsEntity(t.Entity, ctx)
		case *o.TargetRef_Cell:
			if t.Cell == nil || t.Cell.X == nil || t.Cell.Z == nil || t.Cell.GetX() < 0 || t.Cell.GetZ() < 0 {
				return contract("invalid pawn job cell")
			}
		case *o.TargetRef_Unavailable:
			return validateUnavailable(t.Unavailable)
		default:
			return contract("pawn job target missing")
		}
	}
	return nil
}
