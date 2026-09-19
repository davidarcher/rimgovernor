package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type presentationFake struct {
	camera         *p.CameraReply
	selection      *p.SelectionReply
	roster         *p.ColonistRosterReply
	renderState    *p.RenderReply
	calls          int
	hook           func(context.Context) error
	id             *c.Identity
	mapOnly        bool
	mutateIdentity bool
}

func (f *presentationFake) read(ctx context.Context, id *c.Identity) error {
	f.calls++
	if f.mutateIdentity {
		id.LoadToken = proto.String("other")
	}
	f.id = proto.Clone(id).(*c.Identity)
	if f.hook != nil {
		return f.hook(ctx)
	}
	return nil
}
func (f *presentationFake) ReadCamera(ctx context.Context, q *p.ReadRequest) (*p.CameraReply, bridge.Result, error) {
	return f.camera, bridge.Result{}, f.read(ctx, q.Identity)
}
func (f *presentationFake) ReadSelection(ctx context.Context, q *p.ReadRequest) (*p.SelectionReply, bridge.Result, error) {
	return f.selection, bridge.Result{}, f.read(ctx, q.Identity)
}
func (f *presentationFake) ReadColonistRoster(ctx context.Context, q *p.ColonistRosterRequest) (*p.ColonistRosterReply, bridge.Result, error) {
	f.mapOnly = q.CurrentMapOnly != nil && q.GetCurrentMapOnly()
	return f.roster, bridge.Result{}, f.read(ctx, q.Identity)
}
func (f *presentationFake) ReadRenderState(ctx context.Context, q *p.ReadRequest) (*p.RenderReply, bridge.Result, error) {
	return f.renderState, bridge.Result{}, f.read(ctx, q.Identity)
}
func presentationFixture(t *testing.T) (*Server, *presentationFake, *Snapshot) {
	t.Helper()
	identity := &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0), LoadToken: proto.String("load")}
	observed := &c.ObservationContext{Identity: identity, Tick: proto.Int64(5), NativeGeneration: proto.Uint64(math.MaxUint64)}
	f := &presentationFake{camera: &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: observed, ZoomExtensionEnabled: proto.Bool(false)}}}, selection: &p.SelectionReply{Outcome: &p.SelectionReply_Selection{Selection: &p.SelectionSnapshot{Context: observed}}}, roster: &p.ColonistRosterReply{Outcome: &p.ColonistRosterReply_Roster{Roster: &p.ColonistRoster{Context: observed}}}, renderState: &p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: observed, Supported: proto.Bool(true)}}}}
	snapshot := &Snapshot{Connected: true, Identity: domain.Known(observation.Identity{Colony: "colony", Load: "load", Map: 0, Tick: 4})}
	server, err := New(Config{Presentation: f, ReadTimeout: 5 * time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return *snapshot, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { server.Close() })
	return server, f, snapshot
}
func presentationRequest(t *testing.T, s *Server, path string) *httptest.ResponseRecorder {
	t.Helper()
	request := httptest.NewRequest(http.MethodGet, "http://localhost"+path, nil)
	recorder := httptest.NewRecorder()
	s.Handler().ServeHTTP(recorder, request)
	return recorder
}
func TestPresentationOfficialJSONAndFixedMethods(t *testing.T) {
	for _, route := range []string{"camera", "selection", "colonists", "render-state"} {
		t.Run(route, func(t *testing.T) {
			s, f, _ := presentationFixture(t)
			out := presentationRequest(t, s, "/api/presentation/"+route)
			if out.Code != 200 || f.calls != 1 || f.id.GetMapId() != 0 || f.id.GetColonyId() != "colony" {
				t.Fatal(out.Code, out.Body.String())
			}
			if route == "colonists" && !f.mapOnly {
				t.Fatal("roster not restricted to current map")
			}
			if !strings.Contains(out.Body.String(), `"nativeGeneration":"18446744073709551615"`) {
				t.Fatal("uint64 precision lost", out.Body.String())
			}
			if out.Header().Get("Cache-Control") != "no-store" || out.Header().Get("X-Content-Type-Options") != "nosniff" {
				t.Fatal(out.Header())
			}
			if route == "camera" {
				reply := &p.CameraReply{}
				if err := protojson.Unmarshal(out.Body.Bytes(), reply); err != nil {
					t.Fatal(err)
				}
				if reply.GetCamera().RootSize != nil || reply.GetCamera().ZoomExtensionEnabled == nil || reply.GetCamera().GetZoomExtensionEnabled() {
					t.Fatal("optional unknown/false lost")
				}
			}
		})
	}
}
func TestPresentationRequestConfinement(t *testing.T) {
	for _, test := range []struct {
		name, method, path, body, origin, host string
		status                                 int
	}{
		{name: "post", method: "POST", path: "/camera", status: 405},
		{name: "head", method: "HEAD", path: "/selection", status: 405},
		{name: "query", method: "GET", path: "/colonists?currentMapOnly=false", status: 400},
		{name: "emptyquery", method: "GET", path: "/camera?", status: 400},
		{name: "body", method: "GET", path: "/camera", body: "{}", status: 400},
		{name: "origin", method: "GET", path: "/camera", origin: "http://other", status: 403},
		{name: "host", method: "GET", path: "/camera", host: "remote", status: 403},
		{name: "longurl", method: "GET", path: "/camera?" + strings.Repeat("x", 2048), status: 400},
	} {
		t.Run(test.name, func(t *testing.T) {
			s, f, _ := presentationFixture(t)
			q := httptest.NewRequest(test.method, "http://localhost/api/presentation"+test.path, strings.NewReader(test.body))
			if test.origin != "" {
				q.Header.Set("Origin", test.origin)
			}
			if test.host != "" {
				q.Host = test.host
			}
			out := httptest.NewRecorder()
			s.Handler().ServeHTTP(out, q)
			if out.Code != test.status || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String(), f.calls)
			}
		})
	}
	s, f, _ := presentationFixture(t)
	s.config.Presentation = nil
	if out := presentationRequest(t, s, "/api/presentation/camera"); out.Code != 404 || f.calls != 0 {
		t.Fatal(out.Code)
	}
}
func TestPresentationRequiresFreshValidIdentity(t *testing.T) {
	for name, edit := range map[string]func(*Snapshot){"unknown": func(s *Snapshot) { s.Identity = domain.Unknown[observation.Identity]() }, "stale": func(s *Snapshot) { s.Stale = true }, "disconnected": func(s *Snapshot) { s.Connected = false }, "invalid": func(s *Snapshot) { s.Identity = domain.Known(observation.Identity{}) }} {
		t.Run(name, func(t *testing.T) {
			s, f, snapshot := presentationFixture(t)
			edit(snapshot)
			out := presentationRequest(t, s, "/api/presentation/camera")
			if out.Code != 503 || f.calls != 0 {
				t.Fatal(out.Code, f.calls)
			}
		})
	}
}
func TestPresentationRejectsSourceAndWorldChanges(t *testing.T) {
	for name, edit := range map[string]func(*presentationFake, *Snapshot){
		"nil":        func(f *presentationFake, _ *Snapshot) { f.camera = nil },
		"missingarm": func(f *presentationFake, _ *Snapshot) { f.camera = &p.CameraReply{} },
		"failurearm": func(f *presentationFake, _ *Snapshot) {
			f.camera = &p.CameraReply{Outcome: &p.CameraReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String("private source error")}}}
		},
		"missingcontext": func(f *presentationFake, _ *Snapshot) { f.camera.GetCamera().Context = nil },
		"mutatedrequest": func(f *presentationFake, _ *Snapshot) {
			f.mutateIdentity = true
			f.camera.GetCamera().Context.Identity.LoadToken = proto.String("other")
		},
		"wrongworld": func(f *presentationFake, _ *Snapshot) {
			f.camera.GetCamera().Context.Identity.LoadToken = proto.String("other")
		},
		"oldtick": func(f *presentationFake, _ *Snapshot) { f.camera.GetCamera().Context.Tick = proto.Int64(1) },
		"switched": func(f *presentationFake, s *Snapshot) {
			f.hook = func(context.Context) error {
				s.Identity = domain.Known(observation.Identity{Colony: "colony", Load: "new-load", Map: 0})
				return nil
			}
		},
		"unavailable": func(f *presentationFake, _ *Snapshot) {
			f.hook = func(context.Context) error { return errors.New("private source error") }
		},
		"unknownwire": func(f *presentationFake, _ *Snapshot) {
			f.camera.GetCamera().ProtoReflect().SetUnknown([]byte{0xa0, 6, 1})
		},
		"nonfinite": func(f *presentationFake, _ *Snapshot) { f.camera.GetCamera().RootSize = proto.Float64(math.NaN()) },
	} {
		t.Run(name, func(t *testing.T) {
			s, f, snapshot := presentationFixture(t)
			edit(f, snapshot)
			out := presentationRequest(t, s, "/api/presentation/camera")
			if out.Code != 503 || strings.Contains(out.Body.String(), "private") {
				t.Fatal(out.Code, out.Body.String())
			}
			var failure Failure
			if json.Unmarshal(out.Body.Bytes(), &failure) != nil || failure.Code == "" {
				t.Fatal(out.Body.String())
			}
		})
	}
}
func TestPresentationCancellationAndResponseBound(t *testing.T) {
	s, f, _ := presentationFixture(t)
	s.config.ReadTimeout = 20 * time.Millisecond
	f.hook = func(ctx context.Context) error { <-ctx.Done(); return ctx.Err() }
	if out := presentationRequest(t, s, "/api/presentation/camera"); out.Code != 504 {
		t.Fatal(out.Code, out.Body.String())
	}
	s, f, _ = presentationFixture(t)
	s.config.MaxResponseBytes = 256
	f.camera.GetCamera().NativeZoomRange = proto.String(strings.Repeat("x", 300))
	if out := presentationRequest(t, s, "/api/presentation/camera"); out.Code != 503 || !strings.Contains(out.Body.String(), "response_limit") {
		t.Fatal(out.Code, out.Body.String())
	}
	s, f, _ = presentationFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	q := httptest.NewRequest("GET", "http://localhost/api/presentation/camera", nil).WithContext(ctx)
	out := httptest.NewRecorder()
	s.Handler().ServeHTTP(out, q)
	if out.Code != 408 || f.calls != 0 {
		t.Fatal(out.Code, f.calls)
	}
}
