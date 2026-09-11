package httpapi

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"net/http/httptest"
	"strings"
	"testing"
)

type notificationFake struct {
	reply   *p.NotificationsReply
	request *p.NotificationsRequest
	calls   int
	hook    func(context.Context) error
}

func (f *notificationFake) ReadNotifications(ctx context.Context, q *p.NotificationsRequest) (*p.NotificationsReply, bridge.Result, error) {
	f.calls++
	f.request = q
	var err error
	if f.hook != nil {
		err = f.hook(ctx)
	}
	return f.reply, bridge.Result{}, err
}
func notificationFixture(t *testing.T) (*Server, *notificationFake, *Snapshot) {
	s, pf, snapshot := presentationFixture(t)
	unavailable := &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("not readable")}
	f := &notificationFake{reply: &p.NotificationsReply{Outcome: &p.NotificationsReply_Notifications{Notifications: &p.NotificationsSnapshot{Context: pf.camera.GetCamera().Context,
		Letters:  &p.LetterSection{Outcome: &p.LetterSection_Observed{Observed: &p.Letters{Listing: &p.Listing{TotalCount: proto.Uint32(1), ReturnedCount: proto.Uint32(0), Complete: proto.Bool(false), Truncated: proto.Bool(true)}}}},
		Messages: &p.MessageSection{Outcome: &p.MessageSection_Unavailable{Unavailable: unavailable}},
		Alerts:   &p.AlertSection{Outcome: &p.AlertSection_Unavailable{Unavailable: unavailable}},
	}}}}
	s.config.Notifications = f
	s.config.Presentation = nil
	return s, f, snapshot
}
func TestNotificationsOfficialPartialReply(t *testing.T) {
	s, f, _ := notificationFixture(t)
	out := presentationRequest(t, s, "/api/presentation/notifications")
	if out.Code != 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	q := f.request
	if q.Identity.MapId == nil || q.GetIdentity().GetMapId() != 0 || !q.GetIncludeLetters() || !q.GetIncludeMessages() || !q.GetIncludeAlerts() || q.GetLetterLimit() != 40 || q.GetMessageLimit() != 12 || q.GetAlertLimit() != 40 {
		t.Fatal(q)
	}
	got := &p.NotificationsReply{}
	if err := protojson.Unmarshal(out.Body.Bytes(), got); err != nil {
		t.Fatal(err)
	}
	if !proto.Equal(got, f.reply) || !strings.Contains(out.Body.String(), `"18446744073709551615"`) || out.Header().Get("Cache-Control") != "no-store" {
		t.Fatal(out.Body.String(), out.Header())
	}
}
func TestNotificationsConfinement(t *testing.T) {
	for _, tc := range []struct {
		name, method, suffix, body, origin, host string
		code                                     int
	}{
		{name: "post", method: "POST", code: 405}, {name: "head", method: "HEAD", code: 405}, {name: "query", method: "GET", suffix: "?limit=1", code: 400}, {name: "emptyquery", method: "GET", suffix: "?", code: 400}, {name: "body", method: "GET", body: "{}", code: 400}, {name: "origin", method: "GET", origin: "http://other", code: 403}, {name: "host", method: "GET", host: "other", code: 403},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, f, _ := notificationFixture(t)
			q := httptest.NewRequest(tc.method, "http://localhost/api/presentation/notifications"+tc.suffix, strings.NewReader(tc.body))
			if tc.origin != "" {
				q.Header.Set("Origin", tc.origin)
			}
			if tc.host != "" {
				q.Host = tc.host
			}
			out := httptest.NewRecorder()
			s.Handler().ServeHTTP(out, q)
			if out.Code != tc.code || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String(), f.calls)
			}
		})
	}
	s, f, _ := notificationFixture(t)
	s.config.Notifications = nil
	if out := presentationRequest(t, s, "/api/presentation/notifications"); out.Code != 404 || f.calls != 0 {
		t.Fatal(out.Code)
	}
}
func TestNotificationsRejectInvalidSource(t *testing.T) {
	for _, name := range []string{"nil", "missing-arm", "missing-section", "empty-section", "context", "world", "tick", "unknown-wire", "source-error", "world-switch", "request-mutation", "stale", "unknown-identity", "disconnected", "limit", "timeout"} {
		t.Run(name, func(t *testing.T) {
			s, f, snapshot := notificationFixture(t)
			n := f.reply.GetNotifications()
			want := 503
			switch name {
			case "nil":
				f.reply = nil
			case "missing-arm":
				f.reply = &p.NotificationsReply{}
			case "missing-section":
				n.Messages = nil
			case "empty-section":
				n.Messages = &p.MessageSection{}
			case "context":
				n.Context = nil
			case "world":
				n.Context.Identity.LoadToken = proto.String("other")
			case "tick":
				n.Context.Tick = proto.Int64(3)
			case "unknown-wire":
				n.ProtoReflect().SetUnknown([]byte{0xa0, 0x06, 0x01})
			case "source-error":
				f.hook = func(context.Context) error { return errors.New("secret-path") }
			case "world-switch":
				f.hook = func(context.Context) error {
					id, _ := snapshot.Identity.Value()
					id.Load = "other"
					snapshot.Identity = domain.Known(id)
					return nil
				}
			case "request-mutation":
				f.hook = func(context.Context) error {
					f.request.Identity.LoadToken = proto.String("other")
					n.Context.Identity = f.request.Identity
					return nil
				}
			case "stale":
				snapshot.Stale = true
			case "unknown-identity":
				snapshot.Identity = domain.Unknown[observation.Identity]()
			case "disconnected":
				snapshot.Connected = false
			case "limit":
				s.config.MaxResponseBytes = 100
			case "timeout":
				want = 504
				f.hook = func(ctx context.Context) error { <-ctx.Done(); return ctx.Err() }
			}
			out := presentationRequest(t, s, "/api/presentation/notifications")
			if out.Code != want || strings.Contains(out.Body.String(), "secret-path") {
				t.Fatal(out.Code, out.Body.String())
			}
			if (name == "stale" || name == "unknown-identity" || name == "disconnected") && f.calls != 0 {
				t.Fatal("called without identity")
			}
		})
	}
}
