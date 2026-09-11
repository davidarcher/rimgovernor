package main

import (
	"context"
	"encoding/json"
	"errors"
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
)

func TestServeRequiresExplicitReadOnlyLocalConfiguration(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--read-only", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
	if _, err := parseServe(base, io.Discard); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][]string{nil, base[1:], append(append([]string(nil), base...), "--listen", "0.0.0.0:8080"), append(append([]string(nil), base...), "--refresh", "0s"), append(append([]string(nil), base...), "--state", "relative.db"), append(append([]string(nil), base...), "unexpected")} {
		if _, err := parseServe(args, io.Discard); err == nil {
			t.Fatalf("unsafe/incomplete options accepted: %q", args)
		}
	}
}

func TestServeRejectsNonNumericPortsAndInvalidAssets(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--read-only", "--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "trial", "--state", filepath.Join(dir, "state.db")}
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

func (f *serviceFake) ConnectGame(context.Context) (bridge.Result, error) {
	return bridge.Result{}, f.connectErr
}
func (f *serviceFake) Identity(ctx context.Context) (bridge.Result, error) {
	f.active.Add(1)
	defer f.active.Add(-1)
	if f.reads.Add(1) > 2 {
		select {
		case f.entered <- struct{}{}:
		default:
		}
		<-ctx.Done()
		return bridge.Result{}, ctx.Err()
	}
	return bridge.Result{Structured: json.RawMessage(`{"success":true,"colonyId":"colony","loadToken":"load","mapId":0,"tick":123,"observationBatchVersion":1,"placementPreviewBatchVersion":2}`)}, nil
}
func (f *serviceFake) Status(context.Context) (bridge.Result, error) {
	return bridge.Result{Structured: json.RawMessage(`{"success":true,"status":"game_loaded","time":{"paused":true,"forcePaused":false,"timeSpeed":"Paused"},"skipped":[]}`)}, nil
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
