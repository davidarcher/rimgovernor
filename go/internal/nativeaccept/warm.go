package nativeaccept

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// KeepGameEnv keeps the RimWorld process alive between harness binaries
// (issue #91): with it set, Game.Close returns the game to the main menu
// instead of stopping it, and the next OpenGame under the same root attaches
// to the running process (games_start on a running game is an attach) and
// skips the boot and def load, roughly 80s of every run. A suite sets it for
// the whole batch and stops the game once at the end (gamesstop). Mod
// static state is process-scoped and survives the reuse (see
// contracts/native-static-state.md); harnesses asserting on statics run
// without it.
const KeepGameEnv = "RIMGOVERNOR_ACCEPT_KEEP_GAME"

// UnloadTool returns the loaded game to the main menu without saving
// (scripts/fixtures/ShutdownFixture.cs); every fixture build carries it.
const UnloadTool = "test/shutdown_unload"

// KeepGame reports whether KeepGameEnv asks for the process to be kept.
func KeepGame() bool {
	switch os.Getenv(KeepGameEnv) {
	case "", "0", "false", "no":
		return false
	}
	return true
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

	output string
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
	g := &Game{Client: client, Keep: KeepGame(), output: filepath.Join(cfg.Output, "warm")}
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

// Close ends the harness's hold: games_stop and a closed session, or with
// Keep, the main menu and a closed session with the process left running.
// It records what it did on report under "stop" / "stop_error" and
// "game_reuse".
func (g *Game) Close(report Report) {
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	if report != nil {
		report["game_reuse"] = map[string]any{"reused": g.Reused, "kept": g.Keep, "openMs": g.Open.Milliseconds()}
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
