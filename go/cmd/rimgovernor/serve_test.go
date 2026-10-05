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
	base := []string{"--observe", "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if _, err := parseServe(base, io.Discard); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][]string{nil, base[1:], append(append([]string(nil), base...), "--listen", "0.0.0.0:8080"), append(append([]string(nil), base...), "--timeout", "0s"), append(append([]string(nil), base...), "--state", "relative.db"), append(append([]string(nil), base...), "unexpected")} {
		if _, err := parseServe(args, io.Discard); err == nil {
			t.Fatalf("unsafe/incomplete options accepted: %q", args)
		}
	}
}

func TestServeRejectsRelativeFlightRecorderPath(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--observe", "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
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

// A plain serve records under the profile; --observe has no profile and
// records beside its state database; --flight-recorder keeps naming the
// acceptance runner's per-case path.
func TestServeFlightRecorderDefaultsUnderTheProfile(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	base := append(serveBase(dir), "--profile", dir)
	config, err := parseServe(base, io.Discard)
	if err != nil || config.flightRecorder != filepath.Join(dir, "flight", "flight.jsonl") {
		t.Fatalf("default ring: %q %v", config.flightRecorder, err)
	}
	explicit := filepath.Join(dir, "case", "flight.jsonl")
	config, err = parseServe(append(append([]string{}, base...), "--flight-recorder", explicit), io.Discard)
	if err != nil || config.flightRecorder != explicit {
		t.Fatalf("explicit ring: %q %v", config.flightRecorder, err)
	}
	observe := []string{"--observe", "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if config, err = parseServe(observe, io.Discard); err != nil || config.flightRecorder != filepath.Join(dir, "flight", "flight.jsonl") {
		t.Fatalf("--observe recorder: %q %v", config.flightRecorder, err)
	}
}

func TestServePprofIsOffUnlessAsked(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--observe", "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	config, err := parseServe(base, io.Discard)
	if err != nil || config.pprof {
		t.Fatalf("pprof on by default: %+v %v", config, err)
	}
	if config, err = parseServe(append(append([]string{}, base...), "--pprof"), io.Discard); err != nil || !config.pprof {
		t.Fatalf("--pprof not retained: %+v %v", config, err)
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
func (f *serviceFake) Disconnected() <-chan struct{}   { return make(chan struct{}) }
func (f *serviceFake) GameClosed(context.Context) bool { return false }
func (f *serviceFake) Reattach(context.Context) error {
	return errors.New("fake bridge never reattaches")
}
func (f *serviceFake) ConnectWithPoll(context.Context, bridge.Result) (bridge.Result, error) {
	return bridge.Result{}, nil
}

// Tick is the read-only service poll: the initial refresh succeeds, the
// first background poll blocks until the session is cancelled.
func (f *serviceFake) Tick(ctx context.Context) (*lifecyclepb.TickReply, bridge.Result, error) {
	reply, raw, err := f.Identity(ctx)
	if err != nil {
		return nil, raw, err
	}
	return &lifecyclepb.TickReply{Outcome: &lifecyclepb.TickReply_Loaded{Loaded: &lifecyclepb.LoadedTick{Context: reply.GetLoaded().Context, Paused: reply.GetLoaded().Paused}}}, raw, nil
}
func (f *serviceFake) Identity(ctx context.Context) (*lifecyclepb.IdentityReply, bridge.Result, error) {
	f.active.Add(1)
	defer f.active.Add(-1)
	if f.reads.Add(1) > 1 {
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
		t.Fatal("occupied listener opened the bridge")
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
func TestServePresentationReadsUseTheAttachedClient(t *testing.T) {
	for _, building := range []bool{false, true} {
		{
			t.Run(fmt.Sprintf("building=%v", building), func(t *testing.T) {
				dir := t.TempDir()
				fake := &servicePresentationFake{buildingReadFake: &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 1)}}}
				var reads serviceBridge = fake
				config := serveConfig{playerControl: building, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
				ctx, cancel := context.WithCancel(context.Background())
				defer cancel()
				address := make(presentationAddressWriter, 1)
				done := make(chan error, 1)
				go func() {
					if building {
						done <- serveBuildingWithBridge(ctx, config, address, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
							return completeBuildingBridge(reads, unusedBuildingCapabilities{}), nil
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
				expected, calls := 200, int32(1)
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

// The routine window budget is one game day (#584, fixed since #875); the
// retired --clock-window-seconds is refused (#244).
func TestServeClockWindowTicksFlag(t *testing.T) {
	dir := t.TempDir()
	withRoundsFamilies(t, "", false)
	base := append(serveBase(dir), "--profile", dir)
	if _, err := parseServe(append(append([]string(nil), base...), "--clock-window-seconds", "2"), io.Discard); err == nil {
		t.Fatal("accepted the retired --clock-window-seconds")
	}
	config := serviceClockConfig(dir, false, 200, 0)
	if config.Start.MaxTicks != 200 || config.CombatMaxTicks != 200 || config.Start.BlindTickBudget != 0 {
		t.Fatal(config.Start.MaxTicks, config.CombatMaxTicks, config.Start.BlindTickBudget)
	}
	// The blind-tick regulator (#583) is armed per window from the flag.
	if c, err := parseServe(append(append([]string(nil), base...), "--clock-blind-ticks", "300"), io.Discard); err != nil || c.clockBlindTicks != 300 {
		t.Fatalf("blind ticks: %+v %v", c, err)
	} else if config = serviceClockConfig(dir, c.clockTestAcceleration, defaultClockWindowTicks, uint32(c.clockBlindTicks)); config.Start.BlindTickBudget != 300 {
		t.Fatal(config.Start)
	}
	if _, err := parseServe(append(append([]string(nil), base...), "--clock-blind-ticks", "1800001"), io.Discard); err == nil {
		t.Fatal("accepted a blind tick budget past the wire bound")
	}
	for _, boost := range []bool{false, true} {
		config = serviceClockConfig(dir, boost, defaultClockWindowTicks, 0)
		if config.Start.MaxTicks != defaultClockWindowTicks || config.CombatMaxTicks != combatBackstopTicks {
			t.Fatal(boost, config.Start.MaxTicks, config.CombatMaxTicks)
		}
	}
}
