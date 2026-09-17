package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

type snapshotFunc func(context.Context) (Snapshot, error)

func (f snapshotFunc) Snapshot(ctx context.Context) (Snapshot, error) { return f(ctx) }

type planFunc func(context.Context, domain.PlanID) (store.PlanState, error)

func (f planFunc) LoadPlan(ctx context.Context, id domain.PlanID) (store.PlanState, error) {
	return f(ctx, id)
}
func unavailablePlan(context.Context, domain.PlanID) (store.PlanState, error) {
	return store.PlanState{}, store.ErrNotFound
}
func newTestAPI(t *testing.T, provider SnapshotProvider, reader PlanReader) *Server {
	t.Helper()
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, provider, reader)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}
func testHTTP(t *testing.T, api *Server) *httptest.Server {
	t.Helper()
	server := httptest.NewServer(api.Handler())
	t.Cleanup(server.Close)
	return server
}
func get(t *testing.T, url string) (int, []byte) {
	t.Helper()
	response, err := http.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatal(err)
	}
	if response.Header.Get("Cache-Control") != "no-store" {
		t.Fatal("cache allowed")
	}
	return response.StatusCode, body
}
func TestUnknownSnapshotRemainsUnknownAndStale(t *testing.T) {
	server := testHTTP(t, newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan)))
	status, body := get(t, server.URL+"/api/state")
	var state State
	if err := json.Unmarshal(body, &state); err != nil {
		t.Fatal(err)
	}
	if status != 200 || state.Game.Tick != nil || state.Game.Paused != nil || state.Generation != nil || state.ActivePlanID != nil || !state.Game.Stale || state.Mode != "manual" {
		t.Fatalf("unknown became fact: %s", body)
	}
	status, body = get(t, server.URL+"/api/health")
	if status != 200 || !strings.Contains(string(body), `"backend":"go"`) {
		t.Fatal(string(body))
	}
}
func TestObservedSnapshotPreservesFalseAndZero(t *testing.T) {
	identity := observation.Identity{Colony: "colony", Load: "load", Map: 0}
	server := testHTTP(t, newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) {
		return Snapshot{SessionID: "load", Connected: true, Status: "Observed", Identity: domain.Known(identity), Tick: domain.Known(domain.Tick(0)), Paused: domain.Known(false), ObservedAt: domain.Known(time.Unix(100, 0)), ActivePlanID: domain.Known(domain.PlanID("plan"))}, nil
	}), planFunc(unavailablePlan)))
	status, body := get(t, server.URL+"/api/state")
	var state State
	json.Unmarshal(body, &state)
	if status != 200 || state.Game.Tick == nil || *state.Game.Tick != 0 || state.Game.Paused == nil || *state.Game.Paused || state.Game.Stale || state.Generation != nil || state.Identity == nil {
		t.Fatalf("lost known fact: %s", body)
	}
}

type routineStatusFunc func(context.Context) (RoutineStatus, error)

func (f routineStatusFunc) RoutineStatus(ctx context.Context) (RoutineStatus, error) { return f(ctx) }

func TestRoutinesRouteUnavailableWithoutProvider(t *testing.T) {
	server := testHTTP(t, newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan)))
	status, _ := get(t, server.URL+"/api/routines")
	if status != 404 {
		t.Fatalf("routines exposed without a provider: %d", status)
	}
}
func TestRoutinesRouteReportsComposedFamiliesAndReviewCursor(t *testing.T) {
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20,
		Routines: routineStatusFunc(func(context.Context) (RoutineStatus, error) {
			return RoutineStatus{ReviewsEnabled: true, MethodsEnabled: true, ActiveFamilies: []string{"routine-sleeping-plans", "routine-bill-plans"}, LastReviewTick: domain.Tick(42), LastReviewKnown: true}, nil
		})}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	server := testHTTP(t, s)
	status, body := get(t, server.URL+"/api/routines")
	var got routineStatusDTO
	if err := json.Unmarshal(body, &got); err != nil {
		t.Fatal(err)
	}
	if status != 200 || !got.ReviewsEnabled || !got.MethodsEnabled || len(got.ActiveFamilies) != 2 || got.LastReviewTick == nil || *got.LastReviewTick != 42 {
		t.Fatalf("routine status: %s", body)
	}
}
func TestRoutinesRouteRejectsMutationAndUnknownReviewCursor(t *testing.T) {
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20,
		Routines: routineStatusFunc(func(context.Context) (RoutineStatus, error) { return RoutineStatus{}, nil })},
		snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	server := testHTTP(t, s)
	status, body := get(t, server.URL+"/api/routines")
	var got routineStatusDTO
	json.Unmarshal(body, &got)
	if status != 200 || got.LastReviewTick != nil || len(got.ActiveFamilies) != 0 {
		t.Fatalf("unknown review cursor: %s", body)
	}
	response, err := http.Post(server.URL+"/api/routines", "application/json", strings.NewReader("{}"))
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 405 {
		t.Fatalf("routines accepted a mutation: %d", response.StatusCode)
	}
}
func TestPlanReadsRealFreshStore(t *testing.T) {
	database, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	building, _ := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 3}, domain.North, "Granite")
	action, _ := domain.NewBuildingAction("a1", building)
	spec, _ := domain.NewPlan("plan", domain.PlanRevision(1<<60), []domain.Action{action})
	if err := database.CreatePlan(context.Background(), spec); err != nil {
		t.Fatal(err)
	}
	server := testHTTP(t, newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), database))
	status, body := get(t, server.URL+"/api/plan?id=plan")
	var plan Plan
	if err := json.Unmarshal(body, &plan); err != nil {
		t.Fatal(err)
	}
	if status != 200 || plan.Revision != spec.Revision() || len(plan.Actions) != 1 || plan.Actions[0].Building.DefName != "Wall" || plan.Actions[0].Progress.Stage != domain.Pending || plan.Actions[0].Progress.Receipt != nil {
		t.Fatalf("incorrect plan: %s", body)
	}
	if plan.Actions[0].Progress.UnsuccessfulReason != nil || !strings.Contains(string(body), `"unsuccessfulReason":null`) {
		t.Fatalf("unknown reason lost: %s", body)
	}
	ctx := context.Background()
	scope := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: spec.ID(), Revision: spec.Revision()}
	if _, err := database.Prepare(ctx, spec.ID(), action.ID(), scope, 10); err != nil {
		t.Fatal(err)
	}
	if _, err := database.Dispatch(ctx, spec.ID(), action.ID(), scope, 10); err != nil {
		t.Fatal(err)
	}
	if _, err := database.Cancel(ctx, spec.ID(), action.ID()); err != nil {
		t.Fatal(err)
	}
	if _, err := database.Observe(ctx, spec.ID(), domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: scope, Tick: 10, Effect: domain.EffectUnsuccessful, Causality: domain.AfterDispatch, UnsuccessfulReason: domain.OutcomeNotAchieved}, scope); err != nil {
		t.Fatal(err)
	}
	status, body = get(t, server.URL+"/api/plan?id=plan")
	if err := json.Unmarshal(body, &plan); err != nil {
		t.Fatal(err)
	}
	progress := plan.Actions[0].Progress
	if status != 200 || progress.Stage != domain.Cancelled || progress.UnsuccessfulReason == nil || *progress.UnsuccessfulReason != domain.OutcomeNotAchieved || progress.Effect == nil || *progress.Effect != domain.EffectUnsuccessful || progress.Unresolved {
		t.Fatalf("unsuccessful observation lost: %s", body)
	}
	if !strings.Contains(string(body), `"revision":"1152921504606846976"`) || !strings.Contains(string(body), `"attempt":"1"`) {
		t.Fatalf("numeric identity changed: %s", body)
	}
	held, _ := domain.NewBuildingAction("held", building)
	spec2, _ := domain.NewPlan("plan-held", domain.PlanRevision(1), []domain.Action{held})
	if err := database.CreatePlan(ctx, spec2); err != nil {
		t.Fatal(err)
	}
	if _, err := database.Hold(ctx, spec2.ID(), held.ID(), []domain.HeldReason{domain.HeldUnsafeThreat}, 5); err != nil {
		t.Fatal(err)
	}
	status, body = get(t, server.URL+"/api/plan?id=plan-held")
	var heldPlan Plan
	if err := json.Unmarshal(body, &heldPlan); err != nil {
		t.Fatal(err)
	}
	if status != 200 || len(heldPlan.Actions[0].Progress.HeldReasons) != 1 || heldPlan.Actions[0].Progress.HeldReasons[0] != domain.HeldUnsafeThreat || !strings.Contains(string(body), `"heldReasons":["unsafe_threat"]`) {
		t.Fatalf("held reason not surfaced: %s", body)
	}
	// Once the action advances past the hold (here: prepares), the plan read
	// must never again surface the earlier, now-stale hold reason.
	if _, err := database.Prepare(ctx, spec2.ID(), held.ID(), domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: spec2.ID(), Revision: spec2.Revision()}, 5); err != nil {
		t.Fatal(err)
	}
	status, body = get(t, server.URL+"/api/plan?id=plan-held")
	heldPlan = Plan{}
	if err := json.Unmarshal(body, &heldPlan); err != nil {
		t.Fatal(err)
	}
	if status != 200 || len(heldPlan.Actions[0].Progress.HeldReasons) != 0 || strings.Contains(string(body), "heldReasons") {
		t.Fatalf("stale held reason resurfaced: %s", body)
	}
	status, _ = get(t, server.URL+"/api/plan?id=missing")
	if status != 404 {
		t.Fatal(status)
	}
}
func TestRejectsCrossOriginAndUnsupportedRequests(t *testing.T) {
	api := newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) { t.Fatal("unexpected provider read"); return Snapshot{}, nil }), planFunc(unavailablePlan))
	server := testHTTP(t, api)
	for _, tc := range []struct {
		method, path, host, origin, site, body string
		want                                   int
	}{
		{"GET", "/api/state", "evil.example", "", "", "", 403},
		{"GET", "/api/state", "", "http://evil.example", "", "", 403},
		{"GET", "/api/state", "", "", "cross-site", "", 403},
		{"POST", "/api/state", "", "", "", "", 405},
		{"POST", "/api/chat", "", "", "", "{}", 501},
		{"GET", "/api/plan", "", "", "", "", 400},
		{"GET", "/api/plan?id=a&id=b", "", "", "", "", 400},
		{"GET", "/api/state?unexpected=1", "", "", "", "", 400},
		{"GET", "/api/state", "", "", "", "{}", 400},
		{"GET", "/api/missing", "", "", "", "", 404},
	} {
		t.Run(tc.method+tc.path+tc.host+tc.origin+tc.site+tc.body, func(t *testing.T) {
			request, _ := http.NewRequest(tc.method, server.URL+tc.path, strings.NewReader(tc.body))
			if tc.host != "" {
				request.Host = tc.host
			}
			request.Header.Set("Origin", tc.origin)
			request.Header.Set("Sec-Fetch-Site", tc.site)
			response, err := http.DefaultClient.Do(request)
			if err != nil {
				t.Fatal(err)
			}
			defer response.Body.Close()
			if response.StatusCode != tc.want {
				t.Fatal(response.StatusCode)
			}
		})
	}
}
func TestReadFailuresAndResponseBound(t *testing.T) {
	for _, tc := range []struct {
		name     string
		provider snapshotFunc
		limit    int
		want     int
	}{
		{"unavailable", func(context.Context) (Snapshot, error) { return Snapshot{}, errors.New("private storage path") }, 1 << 20, 503},
		{"timeout", func(ctx context.Context) (Snapshot, error) { <-ctx.Done(); return Snapshot{}, ctx.Err() }, 1 << 20, 504},
		{"response bound", func(context.Context) (Snapshot, error) { return Snapshot{Status: strings.Repeat("x", 500)}, nil }, 256, 503},
	} {
		t.Run(tc.name, func(t *testing.T) {
			api := newTestAPI(t, tc.provider, planFunc(unavailablePlan))
			api.config.MaxResponseBytes = tc.limit
			api.config.ReadTimeout = 20 * time.Millisecond
			server := testHTTP(t, api)
			status, body := get(t, server.URL+"/api/state")
			if status != tc.want || len(body) > tc.limit || strings.Contains(string(body), "private storage path") {
				t.Fatalf("%d %s", status, body)
			}
		})
	}
}
func TestRequestCancellationAndHEAD(t *testing.T) {
	observed := make(chan struct{})
	cancelled := make(chan struct{})
	server := testHTTP(t, newTestAPI(t, snapshotFunc(func(ctx context.Context) (Snapshot, error) {
		close(observed)
		<-ctx.Done()
		close(cancelled)
		return Snapshot{}, ctx.Err()
	}), planFunc(unavailablePlan)))
	ctx, cancel := context.WithCancel(context.Background())
	request, _ := http.NewRequestWithContext(ctx, "GET", server.URL+"/api/state", nil)
	done := make(chan struct{})
	go func() {
		response, _ := http.DefaultClient.Do(request)
		if response != nil {
			response.Body.Close()
		}
		close(done)
	}()
	<-observed
	cancel()
	select {
	case <-cancelled:
	case <-time.After(time.Second):
		t.Fatal("provider not cancelled")
	}
	<-done
	request, _ = http.NewRequest("HEAD", server.URL+"/api/health", nil)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(response.Body)
	if response.StatusCode != 200 || len(body) != 0 || response.ContentLength <= 0 {
		t.Fatal("invalid HEAD response")
	}
}
func TestServeLoopbackAndGracefulShutdown(t *testing.T) {
	api := newTestAPI(t, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- api.Serve(ctx, listener) }()
	status, _ := get(t, "http://"+listener.Addr().String()+"/api/health")
	if status != 200 {
		t.Fatal(status)
	}
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("shutdown hung")
	}
	remote := &rejectedListener{address: &net.TCPAddr{IP: net.IPv4zero, Port: 12345}}
	if err := api.Serve(context.Background(), remote); err == nil {
		t.Fatal("nonloopback listener accepted")
	}
	if remote.accepted {
		t.Fatal("nonloopback listener reached Accept")
	}
}

// The rejection path needs an address, not a public socket or firewall permission.
type rejectedListener struct {
	address  net.Addr
	accepted bool
}

func (l *rejectedListener) Addr() net.Addr { return l.address }
func (l *rejectedListener) Close() error   { return nil }
func (l *rejectedListener) Accept() (net.Conn, error) {
	l.accepted = true
	return nil, errors.New("rejected listener must not accept")
}

func TestServeShutdownCancelsActiveProvider(t *testing.T) {
	started := make(chan struct{})
	finished := make(chan struct{})
	api := newTestAPI(t, snapshotFunc(func(ctx context.Context) (Snapshot, error) {
		close(started)
		<-ctx.Done()
		close(finished)
		return Snapshot{}, ctx.Err()
	}), planFunc(unavailablePlan))
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- api.Serve(ctx, listener) }()
	requestDone := make(chan struct{})
	go func() {
		response, _ := http.Get("http://" + listener.Addr().String() + "/api/state")
		if response != nil {
			response.Body.Close()
		}
		close(requestDone)
	}()
	<-started
	cancel()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("active provider was not cancelled")
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	<-requestDone
}

func TestRoutinesRouteExposesDevelopmentRanking(t *testing.T) {
	risk := 1.0
	development := policy.DevelopmentState{Tick: 500, Workers: domain.Known(3), Labor: domain.Known(map[policy.WorkType]int{policy.WorkResearch: 1, policy.WorkConstruction: 0}), Capacity: 2, Committed: []domain.GoalID{"player-room"},
		Rows: []policy.DevelopmentRow{
			{Goal: "ensure-research", Score: 60, Deficit: domain.Known(0.6), WaitingSince: 100, Selected: true},
			{Goal: "ensure-comfort", Score: 40, Deficit: domain.Known(0.5), WaitingSince: 100, Reason: policy.DevelopmentLabor, Bottleneck: policy.WorkConstruction},
			{Goal: "maintain-wood", Score: 0, Deficit: domain.Known(0.3), Risk: domain.Known(risk), WaitingSince: 200, Reason: policy.DevelopmentRisk},
			{Goal: "maintain-resource", WaitingSince: 300, Reason: policy.DevelopmentUnknown},
		}}
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20,
		Routines: routineStatusFunc(func(context.Context) (RoutineStatus, error) {
			return RoutineStatus{ReviewsEnabled: true, LastReviewTick: 500, LastReviewKnown: true, Development: &development}, nil
		})}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	server := testHTTP(t, s)
	status, body := get(t, server.URL+"/api/routines")
	var got routineStatusDTO
	if err := json.Unmarshal(body, &got); err != nil {
		t.Fatal(err)
	}
	d := got.Development
	if status != 200 || d == nil || d.Tick != 500 || d.Workers == nil || *d.Workers != 3 || d.Capacity != 2 || len(d.Committed) != 1 || len(d.Rows) != 4 {
		t.Fatalf("development ranking: %s", body)
	}
	if len(d.Labor) != 2 || d.Labor[0].Work != policy.WorkConstruction || d.Labor[0].Free != 0 || d.Labor[1].Work != policy.WorkResearch || d.Labor[1].Free != 1 {
		t.Fatalf("labor rows unsorted or lost: %s", body)
	}
	if d.Rows[1].Reason != policy.DevelopmentLabor || d.Rows[1].Bottleneck != policy.WorkConstruction || d.Rows[1].Risk != nil {
		t.Fatalf("labor deferral: %s", body)
	}
	if d.Rows[2].Reason != policy.DevelopmentRisk || d.Rows[2].Risk == nil || *d.Rows[2].Risk != 1 || d.Rows[3].Deficit != nil {
		t.Fatalf("risk deferral or unknown deficit: %s", body)
	}
	if !strings.Contains(string(body), `"reason":"labor_unavailable"`) || !strings.Contains(string(body), `"reason":"risk_deferred"`) || !strings.Contains(string(body), `"bottleneck":""`) {
		t.Fatalf("wire reasons: %s", body)
	}
}
