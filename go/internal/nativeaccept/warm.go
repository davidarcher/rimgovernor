package nativeaccept

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// KeepGameEnv controls whether the RimWorld process stays alive between
// harness binaries (issue #91). By default (unset, or anything but
// 0/false/no) Game.Close returns the game to the main menu instead of
// stopping it, and the next OpenGame under the same root attaches to the
// running process (games_start on a running game is an attach) and skips
// the boot and def load, roughly 80s of every run; a batch stops the game
// once at the end (gamesstop). Mod static state is process-scoped and
// survives the reuse (see contracts/native-static-state.md): a harness
// asserting on statics, or one that must see a first-boot process, runs
// with RIMGOVERNOR_ACCEPT_KEEP_GAME=0.
const KeepGameEnv = "RIMGOVERNOR_ACCEPT_KEEP_GAME"

// UnloadTool returns the loaded game to the main menu without saving
// (scripts/fixtures/ShutdownFixture.cs); every fixture build carries it.
const UnloadTool = "test/shutdown_unload"

// KeepGame reports whether the process is kept: true unless KeepGameEnv
// opts out.
func KeepGame() bool { return !envOptsOut(KeepGameEnv) }

// envOptsOut is whether an on-by-default flag is set to 0, false or no.
func envOptsOut(name string) bool {
	switch strings.ToLower(os.Getenv(name)) {
	case "0", "false", "no":
		return true
	}
	return false
}

// Game is one harness's hold on the RimWorld process: a fresh launch, or an
// attach to a process an earlier harness left at the main menu.
type Game struct {
	Client *bridge.Client
	// Reused is true when OpenGame attached to a process already running.
	Reused bool
	// Keep is whether Close leaves the process up for the next harness.
	Keep bool
	// Open is how long OpenGame took, for the report.
	Open time.Duration

	output   string
	cfg      *Config
	released bool
	// keepSkipped is why Close stops a game it would otherwise keep.
	keepSkipped string
}

// OpenGame opens a session on cfg's game the way every harness does
// (GABSExecutable, OpenSession) and, when the process was already running,
// returns it to the main menu so the harness starts from the same state a
// fresh launch would give it. cfg must have been prepared.
func OpenGame(ctx context.Context, cfg *Config) (*Game, error) {
	started := time.Now()
	gabsExecutable, err := GABSExecutable(cfg.Root, cfg.Configuration)
	if err != nil {
		return nil, err
	}
	client, err := bridge.Open(ctx, bridge.ProcessConfig{Executable: gabsExecutable, ConfigDir: cfg.Configuration, GameID: cfg.GameID, Timeout: 60 * time.Second})
	if err != nil {
		return nil, fmt.Errorf("open GABS session: %w", err)
	}
	// Evidence for the unloads goes under its own directory so its numbering
	// never collides with the harness's own.
	g := &Game{Client: client, Keep: KeepGame(), output: filepath.Join(cfg.Output, "warm"), cfg: cfg}
	if status, err := client.GameStatus(ctx); err == nil {
		var state struct {
			Status string `json:"status"`
		}
		if json.Unmarshal(status.Structured, &state) == nil {
			// "shared-running": the process belongs to an earlier harness's
			// session; games_start attaches this one.
			g.Reused = state.Status == "running" || state.Status == "connected" || state.Status == "shared-running"
		}
	}
	launched, err := client.GamesStart(ctx)
	if err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("games_start: %w", err)
	}
	if _, err := client.ConnectWithPoll(ctx, launched); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("connect: %w", err)
	}
	if g.Reused {
		if err := g.toMainMenu(ctx, "warm-open"); err != nil {
			_ = client.Close()
			return nil, fmt.Errorf("reused game: %w", err)
		}
	}
	g.Open = time.Since(started)
	return g, nil
}

// Release closes the harness's GABP session without stopping the game, so
// a rimgovernor serve subprocess can take the sole slot on the same game.
// Reattach (or Close, which reattaches on its own) follows once the service
// has stopped.
//
// A released game is not kept: a process that hosted a service (killed
// with its authority and clock epoch still granted) never keeps the next
// service's authority (stablepatientaccept on such a process loses
// automate mode within 2s of every resume, 3/3 runs, where the same
// harness passes 3/3 on a fresh process and on a process kept by a
// bridge-only harness). Until the native side clears that on unload,
// serve-driven harnesses end with games_stop.
func (g *Game) Release() error {
	if g.released {
		return nil
	}
	g.released = true
	if g.Keep {
		g.Keep = false
		g.keepSkipped = "released to a service: a process that hosted rimgovernor serve does not keep the next service's authority"
	}
	if err := g.Client.Close(); err != nil {
		return fmt.Errorf("release bridge session: %w", err)
	}
	return nil
}

// Reattach reopens the harness's session on the same game after Release,
// retrying for a while because a stopped service's own GABS subprocess
// frees the slot asynchronously. The new client replaces g.Client.
func (g *Game) Reattach(ctx context.Context) (*bridge.Client, error) {
	if !g.released {
		return g.Client, nil
	}
	gabsExecutable, err := GABSExecutable(g.cfg.Root, g.cfg.Configuration)
	if err != nil {
		return nil, err
	}
	client, err := ReopenSession(ctx, gabsExecutable, g.cfg.Configuration, g.cfg.GameID)
	if err != nil {
		return nil, fmt.Errorf("reattach harness session: %w", err)
	}
	g.Client, g.released = client, false
	return client, nil
}

// Close ends the harness's hold: games_stop and a closed session, or with
// Keep, the main menu and a closed session with the process left running.
// It records what it did on report under "stop" / "stop_error" and
// "game_reuse". A released session is reattached first.
func (g *Game) Close(report Report) {
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	if report != nil {
		reuse := map[string]any{"reused": g.Reused, "kept": g.Keep, "openMs": g.Open.Milliseconds()}
		if g.keepSkipped != "" {
			reuse["keepSkipped"] = g.keepSkipped
		}
		report["game_reuse"] = reuse
	}
	if g.released {
		if _, err := g.Reattach(ctx); err != nil {
			if report != nil {
				report["stop_error"] = "reopen session for games_stop: " + err.Error()
			}
			return
		}
	}
	if g.Keep {
		if err := g.toMainMenu(ctx, "warm-close"); err == nil {
			if report != nil {
				report["stop"] = "kept running at the main menu (" + KeepGameEnv + ")"
			}
			_ = g.Client.Close()
			return
		} else if report != nil {
			report["stop_error"] = "keep: " + err.Error()
		}
	}
	if stopped, err := g.Client.GamesStop(ctx); err == nil {
		if report != nil {
			report["stop"] = string(stopped.Envelope)
		}
		awaitStopped(ctx, g.Client)
	} else if report != nil {
		report["stop_error"] = err.Error()
	}
	_ = g.Client.Close()
}

// awaitStopped polls games_status after games_stop until GABS reports the
// process gone (or ctx expires). Without it the next OpenGame under the
// same root can race the teardown: GABS still reports the process
// connected, games_start attaches to it mid-game, and a fixture that needs
// the main menu (test/configure_start) refuses -- observed generating a
// manifest's second variant right after its first.
func awaitStopped(ctx context.Context, client *bridge.Client) {
	for {
		status, err := client.GameStatus(ctx)
		if err != nil {
			return
		}
		var state struct {
			Status string `json:"status"`
		}
		if err := json.Unmarshal(status.Structured, &state); err != nil {
			return
		}
		switch state.Status {
		case "stopped", "stale-runtime-cleaned", "disconnected", "":
			return
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(250 * time.Millisecond):
		}
	}
}

// StopGame stops whatever game root owns (games_stop through the root's own
// GABS configuration, headless first) and waits for the process to be gone;
// a root with nothing running is not an error. It is what ends a batch of
// harnesses that kept the game (KeepGameEnv).
func StopGame(ctx context.Context, root, gameID string) error {
	configDir := filepath.Join(root, "config-headless")
	if _, err := os.Stat(configDir); err != nil {
		configDir = filepath.Join(root, "config")
	}
	gabs, err := GABSExecutable(root, configDir)
	if err != nil {
		return err
	}
	client, err := OpenSession(ctx, gabs, configDir, gameID, 60*time.Second)
	if err != nil {
		return err
	}
	defer client.Close()
	if _, err := client.GamesStop(ctx); err != nil {
		return err
	}
	awaitStopped(ctx, client)
	return nil
}

// toMainMenu unloads whatever is loaded and waits until no game answers.
func (g *Game) toMainMenu(ctx context.Context, label string) error {
	if err := os.MkdirAll(g.output, 0755); err != nil {
		return err
	}
	h := NewHarness(g.Client, g.output)
	loaded, err := gameLoaded(ctx, h, label+"-identity")
	if err != nil {
		return err
	}
	if !loaded {
		return nil
	}
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	if !Contains(names, UnloadTool) {
		return fmt.Errorf("a game is loaded and %s is not installed: build the mod with a fixture or stop the game", UnloadTool)
	}
	reply, err := h.Call(ctx, label+"-unload", UnloadTool, map[string]any{})
	if err != nil {
		return err
	}
	if ok, _ := AsBool(reply["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", UnloadTool, reply)
	}
	return WaitProgress(ctx, Wait{Ceiling: 90 * time.Second, Interval: 500 * time.Millisecond}, func(ctx context.Context) (string, bool, error) {
		loaded, err := gameLoaded(ctx, h, label+"-menu")
		return "", err == nil && !loaded, err
	})
}

// gameLoaded is whether lifecycle_read_identity reports a loaded game; an
// unavailable or failure outcome is the main menu.
func gameLoaded(ctx context.Context, h *Harness, label string) (bool, error) {
	reply, err := h.Wire(ctx, label, "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return false, err
	}
	_, ok := AsMap(reply["loaded"])
	return ok, nil
}
