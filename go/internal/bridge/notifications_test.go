package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"strings"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func notificationsTestListing(n uint32) *p.Listing {
	return &p.Listing{TotalCount: proto.Uint32(n), ReturnedCount: proto.Uint32(n), Complete: proto.Bool(true), Truncated: proto.Bool(false)}
}
func notificationsTestSnapshot() *p.NotificationsSnapshot {
	return &p.NotificationsSnapshot{Context: authorityTestContext(7), Letters: &p.LetterSection{Outcome: &p.LetterSection_Observed{Observed: &p.Letters{Listing: notificationsTestListing(0)}}}, Messages: &p.MessageSection{Outcome: &p.MessageSection_Observed{Observed: &p.Messages{Listing: notificationsTestListing(0)}}}, Alerts: &p.AlertSection{Outcome: &p.AlertSection_Observed{Observed: &p.Alerts{Listing: notificationsTestListing(0)}}}}
}
func TestNotificationsFixedReadAndOptionalSections(t *testing.T) {
	for _, disabled := range []bool{false, true} {
		t.Run(map[bool]string{false: "defaults", true: "explicit false"}[disabled], func(t *testing.T) {
			q := &p.NotificationsRequest{Identity: pbIdentity()}
			snapshot := notificationsTestSnapshot()
			if disabled {
				q.IncludeLetters = proto.Bool(false)
				snapshot.Letters = nil
			}
			snapshot.Messages = &p.MessageSection{Outcome: &p.MessageSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("temporarily unavailable")}}}
			original := proto.Clone(q)
			calls := 0
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				if arg.Tool != "rimgovernor/presentation_notifications" {
					t.Fatal(arg.Tool)
				}
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					t.Fatal(err)
				}
				actual := &p.NotificationsRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), actual); err != nil || !proto.Equal(actual, original) {
					t.Fatal(actual, err)
				}
				return pbResult(&p.NotificationsReply{Outcome: &p.NotificationsReply_Notifications{Notifications: snapshot}}), nil
			}}, time.Second)
			reply, raw, err := client.ReadNotifications(context.Background(), q)
			if err != nil || calls != 1 || len(raw.Envelope) == 0 || !proto.Equal(q, original) || reply.GetNotifications().Messages.GetUnavailable() == nil {
				t.Fatal(reply, err, calls)
			}
			if !reply.GetNotifications().Alerts.GetObserved().Listing.GetComplete() {
				t.Fatal("known empty lost")
			}
		})
	}
}
func TestNotificationsPartialAndUnknownFacts(t *testing.T) {
	v := notificationsTestSnapshot()
	letter := &p.Letter{Id: proto.String("letter-1"), Choices: []*p.LetterChoice{{}, {Index: proto.Uint32(2)}}}
	v.Letters.GetObserved().Letters = []*p.Letter{letter}
	v.Letters.GetObserved().Listing = &p.Listing{TotalCount: proto.Uint32(9), ReturnedCount: proto.Uint32(1), Complete: proto.Bool(false), Truncated: proto.Bool(true)}
	v.Messages.GetObserved().Listing = nil
	if err := notificationsSnapshot(v, &p.NotificationsRequest{Identity: pbIdentity()}); err != nil {
		t.Fatal(err)
	}
	if letter.Dismissible != nil || letter.Choices[0].Index != nil || v.Messages.GetObserved().Listing != nil {
		t.Fatal("unknown facts defaulted")
	}
}
func TestNotificationsMalformedEvidence(t *testing.T) {
	cases := map[string]func(*p.NotificationsSnapshot){
		"wrong world":               func(v *p.NotificationsSnapshot) { v.Context.Identity.LoadToken = proto.String("replacement") },
		"missing context tick":      func(v *p.NotificationsSnapshot) { v.Context.Tick = nil },
		"missing requested section": func(v *p.NotificationsSnapshot) { v.Messages = nil },
		"missing section outcome":   func(v *p.NotificationsSnapshot) { v.Letters = &p.LetterSection{} },
		"bad unavailable": func(v *p.NotificationsSnapshot) {
			v.Messages = &p.MessageSection{Outcome: &p.MessageSection_Unavailable{Unavailable: &c.Unavailable{}}}
		},
		"count mismatch": func(v *p.NotificationsSnapshot) { v.Alerts.GetObserved().Listing.ReturnedCount = proto.Uint32(1) },
		"false complete": func(v *p.NotificationsSnapshot) { v.Alerts.GetObserved().Listing.TotalCount = proto.Uint32(2) },
		"false truncated": func(v *p.NotificationsSnapshot) {
			v.Alerts.GetObserved().Listing.Complete = proto.Bool(false)
			v.Alerts.GetObserved().Listing.Truncated = proto.Bool(true)
		},
		"duplicate IDs": func(v *p.NotificationsSnapshot) {
			v.Letters.GetObserved().Listing = nil
			v.Letters.GetObserved().Letters = []*p.Letter{{Id: proto.String("x")}, {Id: proto.String("x")}}
		},
		"zero choice": func(v *p.NotificationsSnapshot) {
			v.Letters.GetObserved().Listing = nil
			v.Letters.GetObserved().Letters = []*p.Letter{{Choices: []*p.LetterChoice{{Index: proto.Uint32(0)}}}}
		},
		"duplicate choice": func(v *p.NotificationsSnapshot) {
			v.Letters.GetObserved().Listing = nil
			v.Letters.GetObserved().Letters = []*p.Letter{{Choices: []*p.LetterChoice{{Index: proto.Uint32(1)}, {Index: proto.Uint32(1)}}}}
		},
		"default limit": func(v *p.NotificationsSnapshot) {
			v.Messages.GetObserved().Listing = nil
			for i := 0; i < 13; i++ {
				v.Messages.GetObserved().Messages = append(v.Messages.GetObserved().Messages, &p.TransientMessage{})
			}
		},
		"target cell presence": func(v *p.NotificationsSnapshot) {
			v.Alerts.GetObserved().Listing = nil
			v.Alerts.GetObserved().Alerts = []*p.Alert{{Targets: []*p.LookTarget{{Position: &c.Cell{X: proto.Int32(1)}}}}}
		},
	}
	for name, change := range cases {
		t.Run(name, func(t *testing.T) {
			v := notificationsTestSnapshot()
			change(v)
			if err := notificationsSnapshot(v, &p.NotificationsRequest{Identity: pbIdentity()}); err == nil {
				t.Fatal("accepted malformed evidence")
			}
		})
	}
}
func TestNotificationsWireBounds(t *testing.T) {
	for name, value := range map[string]proto.Message{
		"nonfinite":    &p.TransientMessage{Alpha: proto.Float64(math.Inf(1))},
		"UTF8":         &p.Letter{Text: proto.String(string([]byte{255}))},
		"encoded size": &p.Letter{Text: proto.String(strings.Repeat("a", maxProtoBytes))},
	} {
		t.Run(name, func(t *testing.T) {
			if err := notificationsWire(value); err == nil {
				t.Fatal("accepted invalid wire")
			}
		})
	}
	targets := &p.LookTargets{}
	for i := 0; i < 4096; i++ {
		targets.Targets = append(targets.Targets, &p.LookTarget{})
	}
	if err := notificationsWire(targets); err != nil {
		t.Fatal(err)
	}
	targets.Targets = append(targets.Targets, &p.LookTarget{})
	if err := notificationsWire(targets); err == nil {
		t.Fatal("target ceiling ignored")
	}
	unknown := &p.Letter{}
	unknown.ProtoReflect().SetUnknown([]byte{0xf8, 0x07, 0x01})
	if err := notificationsWire(unknown); err == nil {
		t.Fatal("unknown wire field accepted")
	}
}
func TestNotificationsRequestBoundsAndFailure(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		result := pbResult(&p.NotificationsReply{Outcome: &p.NotificationsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum(), Detail: proto.String("world changed")}}})
		result.IsError = true
		return result, nil
	}}, time.Second)
	for _, limit := range []uint32{0, 257} {
		if _, _, err := client.ReadNotifications(context.Background(), &p.NotificationsRequest{Identity: pbIdentity(), LetterLimit: proto.Uint32(limit)}); err == nil {
			t.Fatal("invalid limit")
		}
	}
	if calls != 0 {
		t.Fatal("invalid request reached SDK")
	}
	_, _, err := client.ReadNotifications(context.Background(), &p.NotificationsRequest{Identity: pbIdentity(), LetterLimit: proto.Uint32(256)})
	var refusal *NativeFailure
	if !errors.As(err, &refusal) || calls != 1 {
		t.Fatal(err, calls)
	}
}
