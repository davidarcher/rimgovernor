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

// OpenBridgeSession starts a fresh GABS process, tells it to launch the configured game
// (games_start), then connects and waits for the native tool catalog to be
// discoverable (ConnectWithPoll). On any failure it closes the GABS process before
// returning, so callers never leak a half-open session.
func OpenBridgeSession(ctx context.Context, gabsExecutable, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	return OpenBridgeSessionWith(ctx, bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: configDir, GameID: gameID, Timeout: timeout,
	})
}

// OpenBridgeSessionWith is OpenBridgeSession for a caller that needs the full
// bridge.ProcessConfig (a flight recorder, or the Spawned PID hook).
func OpenBridgeSessionWith(ctx context.Context, config bridge.ProcessConfig) (*bridge.Client, error) {
	config, err := WithRecording(config)
	if err != nil {
		return nil, err
	}
	client, err := bridge.Open(ctx, config)
	if err != nil {
		return nil, fmt.Errorf("open GABS session: %w", err)
	}
	if !GameRunning(ctx, client) {
		if err := prepareFreshLaunch(config.ConfigDir); err != nil {
			_ = client.Close()
			return nil, err
		}
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

// prepareFreshLaunch runs right before a games_start that launches (not
// attaches): the clock journal starts empty, since a kept process owns its
// journal and must find it intact (#119), and the prepared ModsConfig.xml
// and the installed package's hashes are snapshotted so a later OpenGame
// can tell what the process runs with (#166, #209).
func prepareFreshLaunch(configDir string) error {
	if err := ClearStaleClockJournal(configDir); err != nil {
		return err
	}
	if err := RecordLaunchedMods(configDir); err != nil {
		return err
	}
	return RecordLaunchedPackage(configDir)
}

// OpenBridgeSessionWithTakeover attaches even when another GABS session of the same
// root still owns the game. Only for stopping a game whose controller stalled;
// the caller owns both sessions.
func OpenBridgeSessionWithTakeover(ctx context.Context, gabsExecutable, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	config, err := WithRecording(bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: configDir, GameID: gameID, Timeout: timeout,
	})
	if err != nil {
		return nil, err
	}
	client, err := bridge.Open(ctx, config)
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
// bag of fields, always including
// "passed", "scope", "started_at" and, on failure, "error"; Finalize adds
// the timing fields (timing.go).
type Report map[string]any

func NewReport(scope string, headless bool) Report {
	return Report{"passed": false, "headless": headless, "scope": scope, StartedAtKey: time.Now().UTC().Format(time.RFC3339Nano)}
}

// Finalize fills the timing fields and applies the budget (a passing run
// over its budget fails), computes the artifact hash manifest for output
// (Artifacts: capped evidence and the files the report names) and the metrics block (metrics.go), writes result.json, and returns the
// process exit code (0 when report["passed"] is true).
func (r Report) Finalize(output string) int {
	r.finalizeTiming(time.Now())
	if hashes, err := Artifacts(output, r); err == nil {
		r["artifacts"] = hashes
	}
	if _, has := r["wait_stats"]; !has {
		r["wait_stats"] = WaitStats()
	}
	if _, has := r["installed_package"]; !has && installedPackage != nil {
		r["installed_package"] = installedPackage
	}
	r[MetricsKey] = ComputeMetrics(r, output)
	r.Write(output)
	if passed, _ := r["passed"].(bool); passed {
		return 0
	}
	return 1
}

// Write writes the report as output/result.json; Finalize calls it, and a
// runner that adds bookkeeping after Finalize (the series' drift flags)
// calls it again.
func (r Report) Write(output string) {
	data, err := marshalReport(r)
	if err == nil {
		_ = os.WriteFile(filepath.Join(output, "result.json"), data, 0644)
	}
}

// marshalReport encodes the report with "diagnosis" (the failure digest,
// #278) as the first member so it is the first thing read; the remaining
// fields follow in key order as encoding/json writes a map.
func marshalReport(r Report) ([]byte, error) {
	diagnosis, has := r["diagnosis"]
	if !has {
		return json.MarshalIndent(r, "", "  ")
	}
	rest := make(Report, len(r))
	for k, v := range r {
		if k != "diagnosis" {
			rest[k] = v
		}
	}
	head, err := json.MarshalIndent(diagnosis, "  ", "  ")
	if err != nil {
		return nil, err
	}
	tail, err := json.MarshalIndent(rest, "", "  ")
	if err != nil {
		return nil, err
	}
	if len(rest) == 0 {
		return []byte("{\n  \"diagnosis\": " + string(head) + "\n}"), nil
	}
	// tail opens with "{\n  ": the diagnosis member goes in front of its first.
	return append([]byte("{\n  \"diagnosis\": "+string(head)+","), tail[1:]...), nil
}

// Config holds the resolved acceptance-run configuration common to all four binaries.
type Config struct {
	Root          string
	Output        string
	Headless      bool
	GameID        string
	Timeout       time.Duration
	Configuration string // resolved by Prepare/PrepareRendered
	// Expansions are the official expansions to keep active (short names or
	// package IDs); nil defers to ExpansionsEnv, and either way the default is
	// Core-only. Harnesses that test DLC content set it explicitly.
	Expansions []string
	// FixtureOps are the test ops the run's Start calls; OpenSession fills
	// it from the start when unset. The stale-package check names their
	// fixtures in its rebuild hint (#208).
	FixtureOps []string
	// Spawned, when set, is told the PID of each GABS process the game's
	// session launches (bridge.ProcessConfig.Spawned), for a harness that
	// kills its own transport.
	Spawned func(pid int)
	// KeepLoaded leaves a reused process's loaded game in place instead of
	// returning it to the main menu, on open (OpenGame) and on a kept close
	// (Game.Close): a fixture-development session (`acceptance fixture`)
	// works on one loaded world across several calls. The next OpenGame
	// without it unloads as usual.
	KeepLoaded bool
	// RestoreJournal, when set, is a checkpoint bundle whose clock journal
	// replaces the profile's before the game launches: OpenGame stops a
	// kept process, since a running one holds the journal's cursor (#249).
	RestoreJournal string
	// ServiceProfile, when set, is the profile directory every service this
	// session launches uses instead of <Output>/service-profile: a resumed
	// run keeps its bundle's, which the restored store is bound to (#249).
	ServiceProfile string
}

// ServiceProfileDir is the profile directory services launched under c
// use: ServiceProfile when set, else <Output>/service-profile.
func (c *Config) ServiceProfileDir() string {
	if c.ServiceProfile != "" {
		return c.ServiceProfile
	}
	return filepath.Join(c.Output, "service-profile")
}

// PrepareConfig runs Prepare (headless) or PrepareRendered (windowed) against Root
// and records the resolved configuration directory.
func (c *Config) PrepareConfig() error {
	expansions := c.Expansions
	if expansions == nil {
		var err error
		if expansions, err = ExpansionsFromEnv(); err != nil {
			return err
		}
	}
	var configuration string
	var err error
	if c.Headless {
		configuration, err = prepare(c.Root, c.FixtureOps, expansions)
	} else {
		configuration, err = prepareRendered(c.Root, c.FixtureOps, expansions)
	}
	if err != nil {
		return err
	}
	c.Configuration = configuration
	startCache.root, startCache.headless, startCache.expansions = mustAbs(c.Root), c.Headless, expansions
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
