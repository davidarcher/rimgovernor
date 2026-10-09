package nativeaccept

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// BridgeConfig is the bridge.ProcessConfig for gameID under configDir: the
// launch spec is games.<gameID> of configDir/config.json and the running
// game's endpoint record lives under configDir, so every session of one
// configuration finds the same game.
func BridgeConfig(configDir, gameID string, timeout time.Duration) (bridge.ProcessConfig, error) {
	return bridge.ConfiguredProcess(mustAbs(configDir), gameID, timeout)
}

// OpenBridgeSession opens a session, starts the configured game (attaching
// when one is already running under configDir), then connects and waits for
// the native tool catalog to be discoverable (ConnectWithPoll). On any
// failure it closes the session before returning, so callers never leak a
// half-open one.
func OpenBridgeSession(ctx context.Context, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	config, err := BridgeConfig(configDir, gameID, timeout)
	if err != nil {
		return nil, err
	}
	return OpenBridgeSessionWith(ctx, config)
}

// OpenBridgeSessionWith is OpenBridgeSession for a caller that needs the full
// bridge.ProcessConfig (a flight recorder, say).
func OpenBridgeSessionWith(ctx context.Context, config bridge.ProcessConfig) (*bridge.Client, error) {
	config, err := WithRecording(config)
	if err != nil {
		return nil, err
	}
	client, err := bridge.Open(ctx, config)
	if err != nil {
		return nil, fmt.Errorf("open bridge session: %w", err)
	}
	if !GameRunning(ctx, client) {
		if err := prepareFreshLaunch(config.Launch.StateDir); err != nil {
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
// attaches): the prepared ModsConfig.xml
// and the installed package's hashes are snapshotted so a later OpenGame
// can tell what the process runs with.
func prepareFreshLaunch(configDir string) error {
	if err := RecordLaunchedMods(configDir); err != nil {
		return err
	}
	return RecordLaunchedPackage(configDir)
}

// OpenRunningSession connects to the running game without
// starting one. Only for stopping a game whose controller stalled; with no
// owner lease any session may connect, so it is a plain connect.
func OpenRunningSession(ctx context.Context, configDir, gameID string, timeout time.Duration) (*bridge.Client, error) {
	config, err := BridgeConfig(configDir, gameID, timeout)
	if err != nil {
		return nil, err
	}
	config, err = WithRecording(config)
	if err != nil {
		return nil, err
	}
	client, err := bridge.Open(ctx, config)
	if err != nil {
		return nil, fmt.Errorf("open bridge session: %w", err)
	}
	if _, err := client.ConnectGame(ctx); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("connect: %w", err)
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
// process exit code (0 when report["passed"] is true, ExitBreak when the
// run paused at a breakpoint, 1 otherwise).
func (r Report) Finalize(output string) int {
	r.finalizeTiming(time.Now())
	r.collectAttentions(output)
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
	if _, broke := r["break"]; broke {
		return ExitBreak
	}
	return 1
}

// SoftFailuresKey is the report field Expect appends to.
const SoftFailuresKey = "soft_failures"

// Expect records err under check and returns whether it was nil, so a case
// can run several independent assertions over one finished state and see
// every failure in one run instead of the first. A case still returns early
// when a later step depends on the result; the runner fails a case whose
// body returned nil with failures recorded (SoftFailure).
func (r Report) Expect(check string, err error) bool {
	if err == nil {
		return true
	}
	r[SoftFailuresKey] = append(AsSlice(r[SoftFailuresKey]), map[string]any{"check": check, "error": err.Error()})
	return false
}

// SoftFailure is the failures Expect recorded, joined; nil when none.
func (r Report) SoftFailure() error {
	var errs []error
	for _, f := range AsSlice(r[SoftFailuresKey]) {
		row, _ := f.(map[string]any)
		errs = append(errs, fmt.Errorf("%v: %v", row["check"], row["error"]))
	}
	return errors.Join(errs...)
}

// ExitBreak is the exit code of a run paused at a breakpoint: the
// report carries "break", neither "passed" nor "error".
const ExitBreak = 3

// Write writes the report as output/result.json; Finalize calls it, and a
// runner that adds bookkeeping after Finalize (the series' drift flags)
// calls it again.
func (r Report) Write(output string) {
	data, err := marshalReport(r)
	if err == nil {
		_ = os.WriteFile(filepath.Join(output, "result.json"), data, 0644)
	}
}

// marshalReport encodes the report with "diagnosis" (the failure digest) as the first member so it is the first thing read; the remaining
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
	Root     string
	Output   string
	Headless bool
	// Graphics keeps a graphics device in the headless profile (no
	// -nographics): the game still opens no window, but a camera can
	// render on demand (Case.Graphics).
	Graphics      bool
	GameID        string
	Timeout       time.Duration
	Configuration string // resolved by Prepare/PrepareRendered
	// Expansions are the official expansions to keep active (short names or
	// package IDs); nil defers to ExpansionsEnv, and when that is unset every installed
	// expansion loads; an empty non-nil slice pins Core-only.
	Expansions []string
	// FixtureOps are the test ops the run's Start calls; OpenSession fills
	// it from the start when unset. The stale-package check names their
	// fixtures in its rebuild hint.
	FixtureOps []string
	// QuietWorld marks the opened game quiet-world (QuietWorldTool):
	// under the headless profiles' -rimgovernor-test-acceleration launch,
	// wild plants and animals outside the home area stop ticking and the
	// wild spawners stop. The marker persists with the game's saves, so a
	// stage a case saves and reloads keeps it. A production build carries
	// no op; the session then leaves the world live and the report says so.
	QuietWorld bool
	// KeepLoaded leaves a reused process's loaded game in place instead of
	// returning it to the main menu, on open (OpenGame) and on a kept close
	// (Game.Close): a fixture-development session (`acceptance fixture`)
	// works on one loaded world across several calls. The next OpenGame
	// without it unloads as usual.
	KeepLoaded bool
	// Resumed marks a run restored from a checkpoint bundle: its
	// store holds clock cursors from the process that saved it, so OpenGame
	// relaunches a kept process, whose cursors could be older, and the new
	// process's cursors then start past them.
	Resumed bool
	// ServiceProfile, when set, is the profile directory every service this
	// session launches uses instead of <Output>/service-profile: a resumed
	// run keeps its bundle's, which the restored store is bound to.
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
		configuration, err = prepare(c.Root, c.FixtureOps, expansions, c.Graphics)
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
