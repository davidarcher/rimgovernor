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

// OpenSession starts a fresh GABS process, tells it to launch the configured game
// (games_start), then connects and waits for the native tool catalog to be
// discoverable (ConnectWithPoll). On any failure it closes the GABS process before
// returning, so callers never leak a half-open session.
func OpenSession(ctx context.Context, gabsExecutable, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	client, err := bridge.Open(ctx, bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: configDir, GameID: gameID, Timeout: timeout,
	})
	if err != nil {
		return nil, fmt.Errorf("open GABS session: %w", err)
	}
	started, err := client.GamesStart(ctx)
	if err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("games_start: %w", err)
	}
	if _, err := client.ConnectWithPoll(ctx, started); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("connect: %w", err)
	}
	return client, nil
}

// OpenSessionWithTakeover attaches even when another GABS session of the same
// root still owns the game. Only for stopping a game whose controller stalled;
// the caller owns both sessions.
func OpenSessionWithTakeover(ctx context.Context, gabsExecutable, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	client, err := bridge.Open(ctx, bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: configDir, GameID: gameID, Timeout: timeout,
	})
	if err != nil {
		return nil, fmt.Errorf("open GABS session: %w", err)
	}
	if _, err := client.ConnectGameWithTakeover(ctx); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("connect with takeover: %w", err)
	}
	return client, nil
}

// Report is the shared JSON report shape every acceptance binary writes: a dynamic
// bag of fields (mirroring the Python scripts' plain dict report), always including
// "passed", "scope" and, on failure, "error".
type Report map[string]any

func NewReport(scope string, headless bool) Report {
	return Report{"passed": false, "headless": headless, "scope": scope}
}

// Finalize computes the artifact hash manifest for output, writes result.json, and
// returns the process exit code (0 when report["passed"] is true).
func (r Report) Finalize(output string) int {
	if hashes, err := ArtifactHashes(output); err == nil {
		r["artifacts"] = hashes
	}
	data, err := json.MarshalIndent(r, "", "  ")
	if err == nil {
		_ = os.WriteFile(filepath.Join(output, "result.json"), data, 0644)
	}
	if passed, _ := r["passed"].(bool); passed {
		return 0
	}
	return 1
}

// Config holds the resolved acceptance-run configuration common to all four binaries.
type Config struct {
	Root          string
	Output        string
	Headless      bool
	GameID        string
	Timeout       time.Duration
	Configuration string // resolved by Prepare/PrepareRendered
}

// PrepareConfig runs Prepare (headless) or PrepareRendered (windowed) against Root
// and records the resolved configuration directory.
func (c *Config) PrepareConfig() error {
	var configuration string
	var err error
	if c.Headless {
		configuration, err = Prepare(c.Root)
	} else {
		configuration, err = PrepareRendered(c.Root)
	}
	if err != nil {
		return err
	}
	c.Configuration = configuration
	return nil
}

// GameSection reads back games.<GameID> from the resolved configuration directory's
// config.json, e.g. to recover workingDir for PackageFiles.
func (c *Config) GameSection() (map[string]any, error) {
	config, err := loadConfig(filepath.Join(c.Configuration, "config.json"))
	if err != nil {
		return nil, err
	}
	games, ok := config["games"].(map[string]any)
	if !ok {
		return nil, fmt.Errorf("configuration is missing games")
	}
	game, ok := games[c.GameID].(map[string]any)
	if !ok {
		return nil, fmt.Errorf("configuration is missing games.%s", c.GameID)
	}
	return game, nil
}

// StartupLogPath returns the expected Player.log/HeadlessPlayer.log path under Root.
func (c *Config) StartupLogPath() string {
	if c.Headless {
		return filepath.Join(c.Root, "HeadlessPlayer.log")
	}
	return filepath.Join(c.Root, "Player.log")
}
