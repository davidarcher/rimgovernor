package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	commonpb "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	lifecyclepb "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	observationspb "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

func TestServeRequiresExplicitReadOnlyLocalConfiguration(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--observe", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if _, err := parseServe(base, io.Discard); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][]string{nil, base[1:], append(append([]string(nil), base...), "--listen", "0.0.0.0:8080"), append(append([]string(nil), base...), "--refresh", "0s"), append(append([]string(nil), base...), "--state", "relative.db"), append(append([]string(nil), base...), "unexpected")} {
		if _, err := parseServe(args, io.Discard); err == nil {
			t.Fatalf("unsafe/incomplete options accepted: %q", args)
		}
	}
}

func TestServeRejectsRelativeFlightRecorderPath(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--observe", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if _, err := parseServe(append(append([]string{}, base...), "--flight-recorder", "relative.jsonl"), io.Discard); err == nil {
		t.Fatal("accepted relative --flight-recorder path")
	}
	config, err := parseServe(append(append([]string{}, base...), "--flight-recorder", filepath.Join(dir, "timeline.jsonl")), io.Discard)
	if err != nil {
		t.Fatal(err)
	}
	if config.flightRecorder != filepath.Join(dir, "timeline.jsonl") {
		t.Fatalf("flight recorder path not retained: %+v", config)
	}
}

func TestServeWorldEvaluationFlagValidation(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	base := append(serveBase(dir), "--profile", dir)
	c, err := parseServe(append(append([]string(nil), base...), "--world-evaluation-food-margin-days", "1.5"), io.Discard)
	if err != nil || !c.worldEvaluation || c.worldEvaluationFoodMarginDays != 1.5 {
		t.Fatal(c, err)
	}
	if _, err := parseServe(append(append([]string(nil), base...), "--world-evaluation-food-margin-days", "-1"), io.Discard); err == nil {
		t.Fatal("negative food margin days accepted")
	}
}

func TestServeRejectsNonNumericPortsAndInvalidAssets(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--observe", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	for _, port := range []string{"http", "", "-1", "+80", "65536", " 80"} {
		if _, err := parseServe(append(append([]string{}, base...), "--listen", "127.0.0.1:"+port), io.Discard); err == nil {
			t.Errorf("accepted port %q", port)
		}
	}
	for _, listen := range []string{"127.0.0.1:0", "[::1]:65535"} {
		if _, err := parseServe(append(append([]string{}, base...), "--listen", listen), io.Discard); err != nil {
			t.Fatal(err)
		}
	}
	for _, assets := range []string{"relative", filepath.Join(dir, "missing"), dir} {
		if code := serve(context.Background(), append(append([]string{}, base...), "--assets", assets), io.Discard, io.Discard); code != 2 {
			t.Fatalf("assets %q code %d", assets, code)
		}
	}
	if _, err := os.Stat(filepath.Join(dir, "state.db")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("invalid startup touched state: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "index.html"), []byte("dashboard"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := parseServe(append(base, "--assets", dir), io.Discard); err != nil {
		t.Fatal(err)
	}
}

type serviceFake struct {
	reads             atomic.Int32
	active            atomic.Int32
	closed            atomic.Bool
	closeWhileReading atomic.Bool
	entered           chan struct{}
	connectErr        error
}

func (f *serviceFake) GamesStart(context.Context) (bridge.Result, error) {
	return bridge.Result{}, f.connectErr
}
func (f *serviceFake) Disconnected() <-chan struct{} { return make(chan struct{}) }
func (f *serviceFake) Reattach(context.Context) error {
	return errors.New("fake bridge never reattaches")
}
func (f *serviceFake) ConnectWithPoll(context.Context, bridge.Result) (bridge.Result, error) {
	return bridge.Result{}, nil
}
func (f *serviceFake) Identity(ctx context.Context) (*lifecyclepb.IdentityReply, bridge.Result, error) {
	f.active.Add(1)
	defer f.active.Add(-1)
	if f.reads.Add(1) > 2 {
		select {
		case f.entered <- struct{}{}:
		default:
		}
		<-ctx.Done()
		return nil, bridge.Result{}, ctx.Err()
	}
	return &lifecyclepb.IdentityReply{Outcome: &lifecyclepb.IdentityReply_Loaded{Loaded: &lifecyclepb.LoadedIdentity{Context: serviceContext(), Paused: proto.Bool(false)}}}, bridge.Result{}, nil
}
func serviceContext() *commonpb.ObservationContext {
	return &commonpb.ObservationContext{Identity: &commonpb.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(123)}
}
func (f *serviceFake) Status(_ context.Context, identity *commonpb.Identity) (*observationspb.StatusReply, bridge.Result, error) {
	if !proto.Equal(identity, serviceContext().Identity) {
		return nil, bridge.Result{}, errors.New("status scope mismatch")
	}
	return &observationspb.StatusReply{Outcome: &observationspb.StatusReply_Observed{Observed: &observationspb.StatusSnapshot{Context: serviceContext()}}}, bridge.Result{}, nil
}
func (f *serviceFake) Close() error {
	f.closeWhileReading.Store(f.active.Load() != 0)
	f.closed.Store(true)
	return nil
}

type addressWriter chan string

func (w addressWriter) Write(p []byte) (int, error) {
	w <- strings.TrimSpace(strings.TrimPrefix(string(p), "RimGovernor Go read-only service: "))
	return len(p), nil
}

func TestServeAssetsAndCancellationJoinsNativePoll(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "index.html"), []byte("<html>Observation mode</html>"), 0600); err != nil {
		t.Fatal(err)
	}
	fake := &serviceFake{entered: make(chan struct{}, 1)}
	cfg := serveConfig{state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", assets: dir, refresh: 10 * time.Millisecond, bridge: bridge.ProcessConfig{Timeout: time.Second}}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	addresses := make(addressWriter, 1)
	done := make(chan error, 1)
	go func() {
		done <- serveWithBridge(ctx, cfg, addresses, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) { return fake, nil })
	}()
	var address string
	select {
	case address = <-addresses:
	case err := <-done:
		t.Fatalf("startup: %v", err)
	case <-time.After(3 * time.Second):
		t.Fatal("startup timeout")
	}
	client := &http.Client{Timeout: time.Second}
	for _, check := range []struct {
		path, contains string
		code           int
	}{{"/", "Observation mode", 200}, {"/api/health", `"backend":"go"`, 200}, {"/api/state", `"tick":123`, 200}, {"/api/automate", "unsupported", 501}} {
		method := "GET"
		if check.code == 501 {
			method = "POST"
		}
		request, _ := http.NewRequest(method, address+check.path, nil)
		response, err := client.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		body, err := io.ReadAll(response.Body)
		response.Body.Close()
		if err != nil || response.StatusCode != check.code || !strings.Contains(string(body), check.contains) {
			t.Fatalf("%s: %d %s %v", check.path, response.StatusCode, body, err)
		}
	}
	select {
	case <-fake.entered:
	case <-time.After(time.Second):
		t.Fatal("poll did not start")
	}
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("shutdown did not join")
	}
	if !fake.closed.Load() || fake.closeWhileReading.Load() {
		t.Fatal("bridge closed before polling joined")
	}
	if err := os.Remove(cfg.state); err != nil {
		t.Fatalf("database not closed: %v", err)
	}
}

func TestServeStartupFailuresCloseResources(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	cfg := serveConfig{listen: listener.Addr().String(), state: filepath.Join(t.TempDir(), "state.db"), refresh: time.Second}
	opened := false
	err = serveWithBridge(context.Background(), cfg, io.Discard, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) {
		opened = true
		return nil, errors.New("unexpected")
	})
	if err == nil || opened {
		t.Fatal("occupied listener launched GABS")
	}
	cfg.listen = "127.0.0.1:0"
	fake := &serviceFake{connectErr: errors.New("attach failed")}
	err = serveWithBridge(context.Background(), cfg, io.Discard, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) { return fake, nil })
	if !errors.Is(err, fake.connectErr) || !fake.closed.Load() {
		t.Fatal("failed attach leaked", err)
	}
	if _, err := os.Stat(cfg.state); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("failed attach touched state: %v", err)
	}
}

type failedAnnouncement struct{ err error }

func (w failedAnnouncement) Write([]byte) (int, error) { return 0, w.err }

func TestServeFailedAnnouncementClosesStoreAndBridge(t *testing.T) {
	dir := t.TempDir()
	cfg := serveConfig{listen: "127.0.0.1:0", state: filepath.Join(dir, "state.db"), refresh: time.Second}
	fake := &serviceFake{}
	want := errors.New("output closed")
	err := serveWithBridge(context.Background(), cfg, failedAnnouncement{want}, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) { return fake, nil })
	if !errors.Is(err, want) || !fake.closed.Load() || fake.closeWhileReading.Load() {
		t.Fatalf("announcement failure leaked resources: %v", err)
	}
	if err := os.Remove(cfg.state); err != nil {
		t.Fatalf("store remained open: %v", err)
	}
}

type servicePresentationFake struct {
	*buildingReadFake
	presentationCalls atomic.Int32
	notificationCalls atomic.Int32
}

func (f *servicePresentationFake) ReadCamera(context.Context, *p.ReadRequest) (*p.CameraReply, bridge.Result, error) {
	f.presentationCalls.Add(1)
	return &p.CameraReply{Outcome: &p.CameraReply_Camera{Camera: &p.CameraState{Context: serviceContext()}}}, bridge.Result{}, nil
}
func (f *servicePresentationFake) ReadSelection(context.Context, *p.ReadRequest) (*p.SelectionReply, bridge.Result, error) {
	panic("unexpected selection")
}
func (f *servicePresentationFake) ReadColonistRoster(context.Context, *p.ColonistRosterRequest) (*p.ColonistRosterReply, bridge.Result, error) {
	panic("unexpected roster")
}
func (f *servicePresentationFake) ReadRenderState(context.Context, *p.ReadRequest) (*p.RenderReply, bridge.Result, error) {
	panic("unexpected render state")
}

func (f *servicePresentationFake) ReadNotifications(context.Context, *p.NotificationsRequest) (*p.NotificationsReply, bridge.Result, error) {
	f.notificationCalls.Add(1)
	return &p.NotificationsReply{Outcome: &p.NotificationsReply_Notifications{Notifications: &p.NotificationsSnapshot{Context: serviceContext(), Letters: &p.LetterSection{Outcome: &p.LetterSection_Observed{Observed: &p.Letters{}}}, Messages: &p.MessageSection{Outcome: &p.MessageSection_Observed{Observed: &p.Messages{}}}, Alerts: &p.AlertSection{Outcome: &p.AlertSection_Observed{Observed: &p.Alerts{}}}}}}, bridge.Result{}, nil
}

type presentationAddressWriter chan string

func (w presentationAddressWriter) Write(data []byte) (int, error) {
	text := string(data)
	index := strings.Index(text, "http://")
	if index >= 0 {
		w <- strings.TrimSpace(text[index:])
	}
	return len(data), nil
}
func TestServePresentationUsesOptionalAttachedClient(t *testing.T) {
	for _, building := range []bool{false, true} {
		for _, available := range []bool{false, true} {
			t.Run(fmt.Sprintf("building=%v/presentation=%v", building, available), func(t *testing.T) {
				dir := t.TempDir()
				fake := &servicePresentationFake{buildingReadFake: &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 1)}}}
				var reads serviceBridge = fake.buildingReadFake
				if available {
					reads = fake
				}
				config := serveConfig{playerControl: building, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
				ctx, cancel := context.WithCancel(context.Background())
				defer cancel()
				address := make(presentationAddressWriter, 1)
				done := make(chan error, 1)
				go func() {
					if building {
						caps := unusedBuildingCapabilities{}
						done <- serveBuildingWithBridge(ctx, config, address, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
							return buildingServiceBridge{reads: reads, native: caps, authority: caps, writes: caps, draft: unusedDrafts()}, nil
						})
					} else {
						done <- serveWithBridge(ctx, config, address, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) { return reads, nil })
					}
				}()
				var url string
				select {
				case url = <-address:
				case err := <-done:
					t.Fatal(err)
				case <-time.After(3 * time.Second):
					t.Fatal("startup timeout")
				}
				client := http.Client{Timeout: time.Second}
				reply, err := client.Get(url + "/api/presentation/camera")
				if err != nil {
					t.Fatal(err)
				}
				body, err := io.ReadAll(reply.Body)
				reply.Body.Close()
				if err != nil {
					t.Fatal(err)
				}
				expected := 404
				calls := int32(0)
				if available {
					expected = 200
					calls = 1
				}
				if reply.StatusCode != expected || fake.presentationCalls.Load() != calls {
					t.Fatal(reply.StatusCode, string(body), fake.presentationCalls.Load())
				}
				notifications, err := client.Get(url + "/api/presentation/notifications")
				if err != nil {
					t.Fatal(err)
				}
				notificationBody, err := io.ReadAll(notifications.Body)
				notifications.Body.Close()
				if err != nil {
					t.Fatal(err)
				}
				if notifications.StatusCode != expected || fake.notificationCalls.Load() != calls {
					t.Fatal(notifications.StatusCode, string(notificationBody), fake.notificationCalls.Load())
				}
				cancel()
				select {
				case err := <-done:
					if err != nil {
						t.Fatal(err)
					}
				case <-time.After(3 * time.Second):
					t.Fatal("shutdown timeout")
				}
				if !fake.closed.Load() {
					t.Fatal("attached client was not closed")
				}
			})
		}
	}
}

// --clock-window-ticks sizes the colony window (default 2500) within
// 1..maxClockWindowTicks; the combat window never exceeds it.
func TestServeClockWindowTicksFlag(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	base := append(serveBase(dir), "--profile", dir)
	c, err := parseServe(base, io.Discard)
	if err != nil || c.clockWindowTicks != defaultClockWindowTicks {
		t.Fatal(c.clockWindowTicks, err)
	}
	c, err = parseServe(append(append([]string(nil), base...), "--clock-window-ticks", "15000"), io.Discard)
	if err != nil || c.clockWindowTicks != 15000 {
		t.Fatal(c.clockWindowTicks, err)
	}
	for _, bad := range []string{"0", "60001"} {
		if _, err := parseServe(append(append([]string(nil), base...), "--clock-window-ticks", bad), io.Discard); err == nil {
			t.Fatal("accepted --clock-window-ticks", bad)
		}
	}
	config := serviceClockConfig(dir, parseClockSpeed("Normal"), 200)
	if config.Start.MaxTicks != 200 || config.CombatMaxTicks != 200 {
		t.Fatal(config.Start.MaxTicks, config.CombatMaxTicks)
	}
	config = serviceClockConfig(dir, parseClockSpeed("Normal"), defaultClockWindowTicks)
	if config.Start.MaxTicks != defaultClockWindowTicks || config.CombatMaxTicks != combatClockWindowTicks {
		t.Fatal(config.Start.MaxTicks, config.CombatMaxTicks)
	}
}
