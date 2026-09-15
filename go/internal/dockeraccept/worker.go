package dockeraccept

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// baselineSave is the shared acceptance-input store's baseline save, copied
// into every disposable profile (see .rimgovernor/acceptance-inputs' README
// and go/internal/nativeaccept/headless.go's Prepare, which this mirrors).
const baselineSave = "RimGovernor-tribal8-baseline.rws"

// Inputs are the host-absolute, read-only source directories drawn from the
// shared .rimgovernor/acceptance-inputs/<platform> store (see that store's
// README and the recovered docs/developers/testing/local-acceptance-inputs.md,
// deleted alongside the Python acceptance tooling but still the source of
// truth for this layout). Mods is the platform's shared Mods directory
// itself (containing Harmony/RimBridgeServer/RimGovernor as subfolders,
// matching nativeaccept.RequireNativePackage's expectation), even though it
// never actually lives inside Game on the host. Unless WorkerConfig.
// SkipNativeBuild is set, StartWorker treats Mods as a snapshot -- its
// RimGovernor package may predate the source tree it's about to be run
// against -- and rebuilds a fresh one from WorkerConfig.Source via
// BuildNativeMods before ever mounting anything; only that build's private
// mods directory is actually mounted read-only into the container.
// StartWorker mounts Game's top-level entries and (the resolved) Mods
// individually under /worker/game, giving RimWorld a normal-looking install
// tree without ever writing to the shared, read-only input store; see
// gameEntryMounts and the worker Dockerfile stage's comment for why one
// mount for the whole game directory doesn't work. Nothing license-bearing
// (game, mods, GABS binary) ever lives in the image itself.
type Inputs struct {
	Game, Mods, Profile, Gabs string
}

// ConfigTemplate is a host-absolute config.json already shaped for this game
// (its "games.<GameID>" section using the container-internal /inputs/...
// paths this package binds Inputs to below). This package does not
// synthesize a GABS config from scratch -- executablePath, stopProcessName
// and any other GABS-specific keys are the operator's concern, exactly as
// they are for the non-containerized go/internal/nativeaccept harnesses'
// source root. Worker only overlays the batch-mode args and gabsExecutable
// path it controls, the same fields nativeaccept.Prepare rewrites for a
// disposable worker root.
type WorkerConfig struct {
	Docker         string // resolved via DockerBinary
	Image          string // built/inspected image tag or ID
	Inputs         Inputs
	ConfigTemplate string
	Root           string // host output dir; becomes /worker (read-write)
	Name           string // unique container name
	GameID         string
	ClockControl   bool
	StartupTimeout time.Duration
	// Source is the host-absolute repository root containing
	// scripts/build_native_mod.ps1 and integrations/rimgovernor-native, used
	// to rebuild a fresh native mod unless SkipNativeBuild is set. Required
	// unless SkipNativeBuild is set.
	Source string
	// SkipNativeBuild mounts Inputs.Mods directly instead of rebuilding a
	// fresh native package from Source -- only for callers that already
	// know Inputs.Mods is current (e.g. a caller that just built it itself).
	SkipNativeBuild bool
}

// Worker is one running, --network host worker container. BaseURL is
// http://127.0.0.1:Port -- valid *inside the container's own network
// namespace*, which is where rimgovernor's --listen loopback restriction
// requires it to run (see containers/Dockerfile's worker stage comment).
// On a real Linux Docker engine, --network host shares the host's own
// network namespace, so that address is also reachable directly from the
// host. Docker Desktop for Windows/macOS runs containers inside its own
// internal VM, so --network host there only shares the VM's namespace, not
// the true host's -- a host-side HTTP client gets connection refused even
// though the server inside the container is healthy. State/waitReady
// therefore run curl through `docker exec` instead of a host-side HTTP
// client: exec attaches to the container's own namespace, so BaseURL is
// always reachable that way regardless of host platform.
type Worker struct {
	docker  string
	Name    string
	Port    int
	Root    string
	BaseURL string
}

// freeLoopbackPort probes an available host loopback port. It is
// inherently racy against a concurrently starting second worker (the
// listener is closed before docker run claims the port) -- the same
// tradeoff every "let the OS pick a port, then hand it to a child process"
// helper makes; the container is deliberately started under host
// networking, not another Listen, so there is no long-lived process to hold
// it open for us.
func freeLoopbackPort() (int, error) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer listener.Close()
	return listener.Addr().(*net.TCPAddr).Port, nil
}

// prepareConfig validates cfg.Inputs.Mods against the unified native package,
// builds a fresh writable profile at <root>/profile (Prefs.xml/ModsConfig.xml
// copied from cfg.Inputs.Profile and rewritten to activate exactly the
// required mods, plus the baseline save), and copies cfg.ConfigTemplate's
// games.<GameID> section into <root>/config/config.json with its
// container-internal workingDir/args/gabsExecutable overlaid -- the same
// fields nativeaccept.Prepare rewrites for a disposable worker root.
func prepareConfig(cfg WorkerConfig) error {
	if err := nativeaccept.RequireNativePackage(cfg.Inputs.Mods); err != nil {
		return fmt.Errorf("worker mods input: %w", err)
	}

	data, err := os.ReadFile(cfg.ConfigTemplate)
	if err != nil {
		return fmt.Errorf("read config template: %w", err)
	}
	var config map[string]any
	if err := json.Unmarshal(data, &config); err != nil {
		return fmt.Errorf("parse config template: %w", err)
	}
	games, ok := config["games"].(map[string]any)
	if !ok {
		return fmt.Errorf("config template is missing games")
	}
	game, ok := games[cfg.GameID].(map[string]any)
	if !ok {
		return fmt.Errorf("config template is missing games.%s", cfg.GameID)
	}
	if mode, _ := game["launchMode"].(string); mode != "DirectPath" {
		return fmt.Errorf("worker inputs require a DirectPath launch mode")
	}
	delete(game, "stopProcessName")
	target, _ := game["target"].(string)
	game["workingDir"] = "/worker/game"
	game["target"] = "/worker/game/" + filepath.Base(target)
	game["args"] = []any{
		"-savedatafolder=/worker/profile", "-logFile", "/worker/HeadlessPlayer.log",
		"-batchmode", "-nographics", "-rimgovernor-pause-on-load",
	}
	section, _ := config["rimgovernor"].(map[string]any)
	if section == nil {
		section = map[string]any{}
		config["rimgovernor"] = section
	}
	section["gabsExecutable"] = "/inputs/gabs/gabs"

	profileConfigDir := filepath.Join(cfg.Root, "profile", "Config")
	profileSavesDir := filepath.Join(cfg.Root, "profile", "Saves")
	if err := os.MkdirAll(profileConfigDir, 0755); err != nil {
		return err
	}
	if err := os.MkdirAll(profileSavesDir, 0755); err != nil {
		return err
	}
	for _, name := range []string{"Prefs.xml", "ModsConfig.xml"} {
		if err := copyFile(filepath.Join(cfg.Inputs.Profile, "Config", name), filepath.Join(profileConfigDir, name)); err != nil {
			return fmt.Errorf("copy profile %s: %w", name, err)
		}
	}
	if err := copyFile(filepath.Join(cfg.Inputs.Profile, "Saves", baselineSave), filepath.Join(profileSavesDir, baselineSave)); err != nil {
		return fmt.Errorf("copy baseline save: %w", err)
	}
	if err := nativeaccept.PrepareNativeModConfig(filepath.Join(profileConfigDir, "ModsConfig.xml")); err != nil {
		return fmt.Errorf("prepare ModsConfig.xml: %w", err)
	}

	configDir := filepath.Join(cfg.Root, "config")
	if err := os.MkdirAll(configDir, 0755); err != nil {
		return err
	}
	out, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(configDir, "config.json"), out, 0644)
}

func copyFile(source, destination string) error {
	in, err := os.Open(source)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(destination, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// StartWorker prepares cfg.Root, builds the `docker run` invocation and
// starts one --network host worker container, then polls /api/health and
// /api/state until the container reports a connected native session or
// cfg.StartupTimeout elapses. On any failure it removes the container
// before returning, so a caller never leaks a half-started worker.
func StartWorker(ctx context.Context, cfg WorkerConfig) (*Worker, error) {
	if cfg.Name == "" || cfg.Image == "" || cfg.Root == "" || cfg.GameID == "" {
		return nil, fmt.Errorf("worker config requires name, image, root and game id")
	}
	if err := os.MkdirAll(cfg.Root, 0755); err != nil {
		return nil, fmt.Errorf("create worker root: %w", err)
	}
	if !cfg.SkipNativeBuild {
		if cfg.Source == "" {
			return nil, fmt.Errorf("worker config requires source to build a fresh native mod (or set SkipNativeBuild)")
		}
		freshMods, err := BuildNativeMods(ctx, cfg.Source, cfg.Inputs.Game, cfg.Inputs.Mods, cfg.Root, filepath.Join(cfg.Root, "native-build.log"))
		if err != nil {
			return nil, fmt.Errorf("build native mods: %w", err)
		}
		cfg.Inputs.Mods = freshMods
	}
	if err := prepareConfig(cfg); err != nil {
		return nil, fmt.Errorf("prepare worker config: %w", err)
	}
	port, err := freeLoopbackPort()
	if err != nil {
		return nil, fmt.Errorf("allocate loopback port: %w", err)
	}
	gameMounts, err := gameEntryMounts(cfg.Inputs.Game)
	if err != nil {
		return nil, fmt.Errorf("list game inputs: %w", err)
	}
	args := []string{
		"run", "-d", "--name", cfg.Name, "--network", "host", "--init",
		// Mono's Boehm GC installs SIGSEGV-based write-barrier/stack-scan
		// handlers that Docker's default seccomp profile interferes with,
		// crashing RimWorldLinux during early Verse.Root.Start() startup
		// with signo:11 in GC_mark_from. This is the standard fix for
		// Mono-in-Docker.
		"--security-opt", "seccomp=unconfined",
	}
	args = append(args, gameMounts...)
	args = append(args,
		"-v", cfg.Inputs.Mods+":/worker/game/Mods:ro",
		"-v", cfg.Inputs.Gabs+":/inputs/gabs:ro",
		"-v", cfg.Root+":/worker",
		// Consumed by containers/worker-merge-game.sh, which runs
		// `gabs games start` before exec'ing rimgovernor -- see that
		// script's comment for why this can't be a bridge.Client call
		// from this package instead.
		"-e", "GABS_BIN=/inputs/gabs/gabs",
		"-e", "GAME_ID="+cfg.GameID,
		"-e", "GABS_CONFIG_DIR=/worker/config",
		cfg.Image, "serve",
	)
	args = append(args,
		"--player-control",
		"--profile", "/worker/profile",
		"--gabs", "/inputs/gabs/gabs",
		"--config", "/worker/config",
		"--game", cfg.GameID,
		"--state", "/worker/state.db",
		"--assets", "/app/dashboard",
		"--listen", "127.0.0.1:"+strconv.Itoa(port),
		"--flight-recorder", "/worker/flight.jsonl",
		"--timeout", "60s",
	)
	if cfg.ClockControl {
		args = append(args, "--clock-control")
	}
	cmd := exec.CommandContext(ctx, cfg.Docker, args...)
	if out, err := cmd.CombinedOutput(); err != nil {
		return nil, fmt.Errorf("docker run: %w: %s", err, out)
	}
	worker := &Worker{docker: cfg.Docker, Name: cfg.Name, Port: port, Root: cfg.Root, BaseURL: fmt.Sprintf("http://127.0.0.1:%d", port)}
	if err := worker.waitReady(ctx, cfg.StartupTimeout); err != nil {
		_ = worker.Stop(context.Background())
		return nil, err
	}
	return worker, nil
}

// gameEntryMounts returns one read-only `-v` bind mount per top-level entry
// of game, targeting /worker/game/<entry> -- see the worker Dockerfile
// stage's comment for why the whole game directory can't just be one mount.
func gameEntryMounts(game string) ([]string, error) {
	entries, err := os.ReadDir(game)
	if err != nil {
		return nil, err
	}
	mounts := make([]string, 0, len(entries)*2)
	for _, entry := range entries {
		mounts = append(mounts, "-v", filepath.Join(game, entry.Name())+":/worker/game/"+entry.Name()+":ro")
	}
	return mounts, nil
}

type stateDTO struct {
	Connected bool `json:"connected"`
	Game      struct {
		Tick *int64 `json:"tick"`
	} `json:"game"`
}

// curl runs an HTTP request inside the container's own network namespace via
// `docker exec` (see the Worker doc comment for why: a host-side HTTP client
// can't reach a Docker-Desktop-for-Windows/macOS --network host container's
// port, only exec can). It appends the response status code after a newline
// (curl's -w) so the caller gets both the body and the status from one exec.
func (w *Worker) curl(ctx context.Context, method, path string, headers map[string]string, body string) ([]byte, int, error) {
	args := []string{"exec", w.Name, "curl", "-sS", "-X", method, "-w", "\n%{http_code}"}
	for key, value := range headers {
		args = append(args, "-H", key+": "+value)
	}
	if body != "" {
		args = append(args, "-d", body)
	}
	args = append(args, w.BaseURL+path)
	cmd := exec.CommandContext(ctx, w.docker, args...)
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		return nil, 0, fmt.Errorf("docker exec curl %s %s: %w: %s", method, path, err, stderr.String())
	}
	out := stdout.Bytes()
	split := bytes.LastIndexByte(out, '\n')
	if split < 0 {
		return nil, 0, fmt.Errorf("docker exec curl %s %s: no status code in output: %s", method, path, out)
	}
	status, err := strconv.Atoi(strings.TrimSpace(string(out[split+1:])))
	if err != nil {
		return nil, 0, fmt.Errorf("docker exec curl %s %s: %w", method, path, err)
	}
	return out[:split], status, nil
}

// State returns the worker's current /api/state.
func (w *Worker) State(ctx context.Context) (stateDTO, error) {
	var out stateDTO
	body, status, err := w.curl(ctx, http.MethodGet, "/api/state", nil, "")
	if err != nil {
		return out, err
	}
	if status != 200 {
		return out, fmt.Errorf("GET /api/state: unexpected status %d: %s", status, body)
	}
	return out, json.Unmarshal(body, &out)
}

// loadBaselineSave loads the shared acceptance-input store's baseline save
// (already staged at /worker/profile/Saves by prepareConfig) through the
// player HTTP API, exactly as a real player-control client would: rimgovernor
// itself never auto-loads a save on cold start (its own README documents
// --start-save as belonging to the deleted Python acceptance CLI, not
// `rimgovernor serve`), so without this call the game sits at its main menu
// forever and /api/state.connected -- which reflects an observed, loaded
// colony identity, not mere bridge/process liveness -- never turns true.
func (w *Worker) loadBaselineSave(ctx context.Context) error {
	sessionBody, sessionStatus, err := w.curl(ctx, http.MethodGet, "/api/player/session", nil, "")
	if err != nil {
		return fmt.Errorf("player session: %w", err)
	}
	if sessionStatus != 200 {
		return fmt.Errorf("GET /api/player/session: unexpected status %d: %s", sessionStatus, sessionBody)
	}
	var session struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(sessionBody, &session); err != nil {
		return fmt.Errorf("parse player session: %w", err)
	}
	saveName, _ := strings.CutSuffix(baselineSave, ".rws")
	requestBody, err := json.Marshal(map[string]any{
		"requestId": fmt.Sprintf("dockerworkeraccept-load-%d", time.Now().UnixNano()),
		"saveName":  saveName,
		"readiness": "map",
		"timeoutMs": 100000,
	})
	if err != nil {
		return err
	}
	loadBody, loadStatus, err := w.curl(ctx, http.MethodPost, "/api/lifecycle/load",
		map[string]string{"Content-Type": "application/json", "X-RimGovernor-Player": session.Token}, string(requestBody))
	if err != nil {
		return fmt.Errorf("lifecycle load: %w", err)
	}
	if loadStatus != 201 {
		return fmt.Errorf("POST /api/lifecycle/load: unexpected status %d: %s", loadStatus, loadBody)
	}
	return nil
}

// waitReady polls /api/health until the container's HTTP server is up, loads
// the baseline save (once) through the player API, then polls /api/state
// until the container reports a connected native session, mirroring the
// deleted container_native_acceptance.py's start()/api('/api/state') poll
// loop plus the explicit load step described on loadBaselineSave.
func (w *Worker) waitReady(ctx context.Context, timeout time.Duration) error {
	if timeout <= 0 {
		timeout = 240 * time.Second
	}
	deadline := time.Now().Add(timeout)
	var lastErr error
	loadTriggered := false
	for time.Now().Before(deadline) {
		if running, err := ContainerState(ctx, w.docker, w.Name); err != nil {
			lastErr = err
		} else if !running {
			logs, _ := ContainerLogs(ctx, w.docker, w.Name)
			return fmt.Errorf("worker container exited during startup: %s", logs)
		}
		if _, healthStatus, err := w.curl(ctx, http.MethodGet, "/api/health", nil, ""); err == nil && healthStatus == 200 {
			if !loadTriggered {
				// A healthy /api/health only means the HTTP server is up, not
				// that the background read-state poller (controller.ReadState,
				// wired in cmd/rimgovernor/serve_building.go) has completed its
				// first cycle yet -- until it has, /api/lifecycle/load's own
				// snapshot read fails with a generic 503 "Controller data is
				// unavailable" that looks identical to a real failure. Retry
				// rather than latching on the first attempt; only stop once it
				// actually succeeds.
				if err := w.loadBaselineSave(ctx); err != nil {
					lastErr = err
				} else {
					loadTriggered = true
				}
			}
			if state, err := w.State(ctx); err == nil && state.Connected {
				return nil
			} else if err != nil {
				lastErr = err
			}
		} else if err != nil {
			lastErr = err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
	return fmt.Errorf("worker did not report a connected native session within %s: %w", timeout, lastErr)
}

// Stop saves the container's final combined logs to <root>/container.log
// before removing it (no --rm: a container that exits or crashes during
// startup must stay inspectable until this explicit cleanup runs) and
// returns any removal error, so a caller can retain post-mortem evidence even
// when cleanup itself fails.
func (w *Worker) Stop(ctx context.Context) error {
	logs, _ := ContainerLogs(ctx, w.docker, w.Name)
	if logPath := filepath.Join(w.Root, "container.log"); logPath != "" {
		_ = os.WriteFile(logPath, []byte(logs), 0644)
	}
	return RemoveContainer(ctx, w.docker, w.Name)
}
