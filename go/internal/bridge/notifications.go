package bridge

import (
	"context"
	"errors"
	"math"
	"strings"
	"unicode/utf8"

	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// ReadNotifications only observes. Partial listings and unknown optional facts
// remain partial or unknown; this read never acknowledges or dismisses anything.
func (client *Client) ReadNotifications(ctx context.Context, request *p.NotificationsRequest) (*p.NotificationsReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("notification request required")
	}
	request = proto.Clone(request).(*p.NotificationsRequest)
	if err := errors.Join(ValidateIdentity(request.Identity), notificationsWire(request)); err != nil {
		return nil, Result{}, err
	}
	for _, limit := range []*uint32{request.LetterLimit, request.MessageLimit, request.AlertLimit} {
		if limit != nil && (*limit < 1 || *limit > 256) {
			return nil, Result{}, contract("notification limit outside 1..256")
		}
	}
	reply := &p.NotificationsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/presentation_notifications", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = notificationsWire(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.NotificationsReply_Failure:
		err = failure(v.Failure, raw)
	case *p.NotificationsReply_Notifications:
		err = notificationsSnapshot(v.Notifications, request)
	default:
		err = contract("notification outcome required")
	}
	return reply, raw, err
}

func notificationsSnapshot(v *p.NotificationsSnapshot, q *p.NotificationsRequest) error {
	if v == nil {
		return contract("notification snapshot required")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, q.Identity) {
		return contract("notification world mismatch")
	}
	included := func(flag *bool) bool { return flag == nil || *flag }
	if (v.Letters != nil) != included(q.IncludeLetters) || (v.Messages != nil) != included(q.IncludeMessages) || (v.Alerts != nil) != included(q.IncludeAlerts) {
		return contract("notification section inclusion mismatch")
	}
	limit := func(value *uint32, fallback int) int {
		if value == nil {
			return fallback
		}
		return int(*value)
	}
	if v.Letters != nil {
		switch s := v.Letters.Outcome.(type) {
		case *p.LetterSection_Unavailable:
			if err := validateUnavailable(s.Unavailable); err != nil {
				return err
			}
		case *p.LetterSection_Observed:
			if s.Observed == nil {
				return contract("letters missing")
			}
			rows := s.Observed.Letters
			if len(rows) > limit(q.LetterLimit, 40) {
				return contract("letter limit exceeded")
			}
			if err := notificationsListing(s.Observed.Listing, len(rows)); err != nil {
				return err
			}
			seen := map[string]bool{}
			for _, row := range rows {
				if row == nil {
					return contract("nil letter")
				}
				if err := notificationsID(row.Id, seen); err != nil {
					return err
				}
				if row.GetAgeTicks() < 0 {
					return contract("negative letter time")
				}
				if err := notificationsTargets(row.LookTargets); err != nil {
					return err
				}
				if err := notificationsListing(row.ChoicesListing, len(row.Choices)); err != nil {
					return err
				}
				indices := map[uint32]bool{}
				for _, choice := range row.Choices {
					if choice == nil {
						return contract("nil letter choice")
					}
					if choice.Index != nil {
						if choice.GetIndex() == 0 || indices[choice.GetIndex()] {
							return contract("invalid or duplicate letter choice index")
						}
						indices[choice.GetIndex()] = true
					}
				}
			}
		default:
			return contract("letter section outcome required")
		}
	}
	if v.Messages != nil {
		switch s := v.Messages.Outcome.(type) {
		case *p.MessageSection_Unavailable:
			if err := validateUnavailable(s.Unavailable); err != nil {
				return err
			}
		case *p.MessageSection_Observed:
			if s.Observed == nil {
				return contract("messages missing")
			}
			rows := s.Observed.Messages
			if len(rows) > limit(q.MessageLimit, 12) {
				return contract("message limit exceeded")
			}
			if err := notificationsListing(s.Observed.Listing, len(rows)); err != nil {
				return err
			}
			seen := map[string]bool{}
			for _, row := range rows {
				if row == nil {
					return contract("nil message")
				}
				if err := notificationsID(row.Id, seen); err != nil {
					return err
				}
				if row.GetAgeTicks() < 0 {
					return contract("negative message tick age")
				}
				if err := notificationsTargets(row.LookTargets); err != nil {
					return err
				}
			}
		default:
			return contract("message section outcome required")
		}
	}
	if v.Alerts != nil {
		switch s := v.Alerts.Outcome.(type) {
		case *p.AlertSection_Unavailable:
			if err := validateUnavailable(s.Unavailable); err != nil {
				return err
			}
		case *p.AlertSection_Observed:
			if s.Observed == nil {
				return contract("alerts missing")
			}
			rows := s.Observed.Alerts
			if err := notificationsID(s.Observed.SnapshotFingerprint, map[string]bool{}); err != nil {
				return err
			}
			if len(rows) > limit(q.AlertLimit, 40) {
				return contract("alert limit exceeded")
			}
			if err := notificationsListing(s.Observed.Listing, len(rows)); err != nil {
				return err
			}
			seen := map[string]bool{}
			for _, row := range rows {
				if row == nil {
					return contract("nil alert")
				}
				if err := notificationsID(row.Id, seen); err != nil {
					return err
				}
				if err := notificationsListing(row.Listing, len(row.Targets)); err != nil {
					return err
				}
				for _, target := range row.Targets {
					if err := notificationsTarget(target); err != nil {
						return err
					}
				}
				if row.ReadIssue != nil {
					if err := validateUnavailable(row.ReadIssue); err != nil {
						return err
					}
				}
			}
		default:
			return contract("alert section outcome required")
		}
	}
	return nil
}

// Absent metadata is unknown, never an implicit complete-empty listing.
func notificationsListing(v *p.Listing, count int) error {
	if v == nil {
		return nil
	}
	if v.ReturnedCount != nil && int(v.GetReturnedCount()) != count || v.TotalCount != nil && uint64(v.GetTotalCount()) < uint64(count) {
		return contract("notification listing count mismatch")
	}
	if v.GetComplete() && (v.GetTruncated() || v.TotalCount == nil || v.ReturnedCount == nil || v.GetTotalCount() != v.GetReturnedCount()) {
		return contract("notification listing falsely complete")
	}
	if v.GetTruncated() && v.TotalCount != nil && uint64(v.GetTotalCount()) <= uint64(count) {
		return contract("notification listing falsely truncated")
	}
	return nil
}
func notificationsID(id *string, seen map[string]bool) error {
	if id == nil {
		return nil
	} // A missing native identity stays unavailable for control.
	if strings.TrimSpace(*id) == "" || len(*id) > 256 || strings.ContainsRune(*id, 0) || seen[*id] {
		return contract("invalid or duplicate notification identity")
	}
	seen[*id] = true
	return nil
}
func notificationsTargets(v *p.LookTargets) error {
	if v == nil {
		return nil
	}
	if err := notificationsListing(v.Listing, len(v.Targets)); err != nil {
		return err
	}
	if v.Primary != nil {
		if err := notificationsTarget(v.Primary); err != nil {
			return err
		}
	}
	for _, target := range v.Targets {
		if err := notificationsTarget(target); err != nil {
			return err
		}
	}
	return nil
}
func notificationsTarget(v *p.LookTarget) error {
	if v == nil {
		return contract("nil notification target")
	}
	if err := notificationsID(v.Id, map[string]bool{}); err != nil {
		return err
	}
	if err := notificationsID(v.WorldTileId, map[string]bool{}); err != nil {
		return err
	}
	if v.MapId != nil && v.GetMapId() < 0 {
		return contract("invalid notification target map")
	}
	if v.Position != nil && (v.Position.X == nil || v.Position.Z == nil || v.Position.GetX() < 0 || v.Position.GetZ() < 0) {
		return contract("invalid notification target cell")
	}
	return nil
}
func notificationsWire(value proto.Message) error {
	if value == nil || !value.ProtoReflect().IsValid() {
		return contract("notification message required")
	}
	targets := 0
	var walk func(protoreflect.Message, int) error
	walk = func(m protoreflect.Message, depth int) error {
		if !m.IsValid() || depth > 32 || len(m.GetUnknown()) != 0 {
			return contract("invalid notification fields")
		}
		if m.Descriptor().FullName() == "rimgovernor.presentation.v1.LookTarget" {
			targets++
			if targets > 4096 {
				return contract("notification target ceiling exceeded")
			}
		}
		var err error
		m.Range(func(f protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			check := func(v protoreflect.Value) error {
				switch f.Kind() {
				case protoreflect.MessageKind:
					return walk(v.Message(), depth+1)
				case protoreflect.StringKind:
					if !utf8.ValidString(v.String()) {
						return contract("invalid notification UTF-8")
					}
				case protoreflect.DoubleKind, protoreflect.FloatKind:
					if math.IsNaN(v.Float()) || math.IsInf(v.Float(), 0) {
						return contract("nonfinite notification number")
					}
				}
				return nil
			}
			if f.IsList() {
				for i := 0; i < v.List().Len(); i++ {
					if err = check(v.List().Get(i)); err != nil {
						break
					}
				}
			} else {
				err = check(v)
			}
			return err == nil
		})
		return err
	}
	if err := walk(value.ProtoReflect(), 0); err != nil {
		return err
	}
	encoded, err := protojson.Marshal(value)
	if err != nil {
		return contract("invalid notification encoding")
	}
	if len(encoded) > maxProtoBytes {
		return contract("notification encoded size exceeded")
	}
	return nil
}
