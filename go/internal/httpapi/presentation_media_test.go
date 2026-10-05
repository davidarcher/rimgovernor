package httpapi

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

type presentationMediaFake struct {
	render *p.RenderReply
	err    error
	calls  int
	seen   proto.Message
}

func (f *presentationMediaFake) DemandRendering(ctx context.Context, q *p.RenderDemand) (*p.RenderReply, bridge.Result, error) {
	f.calls++
	f.seen = q
	return f.render, bridge.Result{}, f.err
}

func presentationMediaAPI(t *testing.T) (*Server, *presentationMediaFake, string) {
	t.Helper()
	db, e := store.Open(context.Background(), storetest.Path(t))
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { db.Close() })
	identity := &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}
	observed := &c.ObservationContext{Identity: identity, Tick: proto.Int64(5)}
	media := &presentationMediaFake{
		render: &p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: observed, Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(5000)}}},
	}
	f := &playerFixture{journal: db}
	snapshot := Snapshot{Connected: true, Identity: domain.Known(observation.Identity{Colony: "colony", Load: "load", Map: 0, Tick: 5})}
	s, e := NewWithPlayer(Config{Presentation: &presentationFake{camera: &p.CameraReply{}}, PresentationMedia: media, ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20},
		snapshotFunc(func(context.Context) (Snapshot, error) { return snapshot, nil }), planFunc(unavailablePlan), f, db)
	if e != nil {
		t.Fatal(e)
	}
	token := playerCall(s, "GET", "/api/player/session", "", "")
	var session struct{ Token string }
	if err := json.Unmarshal(token.Body.Bytes(), &session); err != nil || session.Token == "" {
		t.Fatal(token.Body.String(), err)
	}
	return s, media, session.Token
}

func TestPresentationMediaRenderDemand(t *testing.T) {
	s, f, token := presentationMediaAPI(t)
	out := playerCall(s, "POST", "/api/presentation/render-demand", `{"leaseSeconds":5}`, token)
	if out.Code != 200 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var status RenderStatusDTO
	if err := json.Unmarshal(out.Body.Bytes(), &status); err != nil || !status.Supported || status.RemainingLeaseMs != 5000 {
		t.Fatal(out.Body.String(), err)
	}
	request, ok := f.seen.(*p.RenderDemand)
	if !ok || request.GetLeaseSeconds() != 5 || request.GetViewer().GetIdentity().GetColonyId() != "colony" {
		t.Fatal(f.seen)
	}
}
func TestPresentationMediaRequiresPlayerToken(t *testing.T) {
	s, f, _ := presentationMediaAPI(t)
	out := playerCall(s, "POST", "/api/presentation/render-demand", `{"leaseSeconds":1}`, "")
	if out.Code != 403 || f.calls != 0 {
		t.Fatal(out.Code, f.calls)
	}
	out = playerCall(s, "POST", "/api/presentation/render-demand", `{"leaseSeconds":1}`, "wrong-token")
	if out.Code != 403 || f.calls != 0 {
		t.Fatal(out.Code, f.calls)
	}
}
func TestPresentationMediaValidation(t *testing.T) {
	for _, tc := range []struct {
		name, path, body string
	}{
		{"missingLease", "/api/presentation/render-demand", `{}`},
		{"oversizedLease", "/api/presentation/render-demand", `{"leaseSeconds":31}`},
		{"unknownField", "/api/presentation/render-demand", `{"leaseSeconds":1,"extra":true}`},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s, f, token := presentationMediaAPI(t)
			out := playerCall(s, "POST", tc.path, tc.body, token)
			if out.Code != 400 || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String())
			}
		})
	}
}
func TestPresentationMediaMethodAndContentType(t *testing.T) {
	s, f, token := presentationMediaAPI(t)
	get := playerCall(s, "GET", "/api/presentation/render-demand", "", token)
	if get.Code != 405 || f.calls != 0 {
		t.Fatal(get.Code)
	}
	out := playerCall(s, "POST", "/api/presentation/render-demand", `{"leaseSeconds":1}`, token)
	if out.Code != 200 {
		t.Fatal(out.Code, out.Body.String())
	}
}
func TestPresentationMediaUnavailableWithoutConfig(t *testing.T) {
	db, e := store.Open(context.Background(), storetest.Path(t))
	if e != nil {
		t.Fatal(e)
	}
	defer db.Close()
	f := &playerFixture{journal: db}
	s, e := NewWithPlayer(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan), f, db)
	if e != nil {
		t.Fatal(e)
	}
	token := playerCall(s, "GET", "/api/player/session", "", "")
	var session struct{ Token string }
	_ = json.Unmarshal(token.Body.Bytes(), &session)
	out := playerCall(s, "POST", "/api/presentation/render-demand", `{"leaseSeconds":1}`, session.Token)
	if out.Code != 404 {
		t.Fatal(out.Code, out.Body.String())
	}
}
