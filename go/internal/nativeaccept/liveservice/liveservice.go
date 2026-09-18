// Package liveservice runs the real Go player service against a loaded save
// for native acceptance harnesses that verify autopilot outcomes: it loads
// the save through a private bridge session, releases the sole GABP slot,
// launches `rimgovernor serve` (na.Serve), acquires player authority and
// keeps it granted, and hands the harness the service's HTTP API and
// durable store. The service can be stopped and started again on the same
// state path to exercise restart reconciliation, and the bridge session
// reopened once the service is down for native reads and the final
// Close. It is a thin wrapper over na.Serve for the harnesses that
// predate it (#138).
package liveservice

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Config names the disposable root, the save and the service binary.
type Config struct {
	Root, Output, GameID string
	Headless             bool
	Binary               string
	Save                 string
	NativeTimeout        time.Duration
	// ClockSpeed is the harness's own serve --clock-speed default, applied
	// only while RIMGOVERNOR_ACCEPT_CLOCK_SPEED is unset (na.ClockSpeedArgs
	// otherwise decides, #128).
	ClockSpeed string
	// Families is RIMGOVERNOR_ROUTINE_FAMILIES; empty runs the autonomous
	// default with every family on.
	Families string
	Prefix   string
	// Debug sets RIMGOVERNOR_CLOCK_DEBUG=1 so the service's stderr traces each
	// scheduler step and planner result.
	Debug bool
	// BeforeService, when set, runs against the loaded, paused save through
	// the private bridge session before it is released to the service: the
	// place for a harness's own in-game setup (a player edit the run then
	// has to live with). facts is the home/colony_facts reply.
	BeforeService func(ctx context.Context, h *na.Harness, identity, facts map[string]any) error
}

// Prepared is a loaded save whose bridge session has been released so the
// service can attach. The game itself is held through na.Game.
type Prepared struct {
	cfg       Config
	naCfg     *na.Config
	game      *na.Game
	last      *Service
	Identity  map[string]any
	StatePath string
}

// Service is one running `rimgovernor serve` process with granted authority.
type Service struct {
	*na.ServiceProcess
}

// Prepare loads cfg.Save, dismisses the colony naming dialog, records the
// loaded identity and closes the bridge session without stopping the game.
func Prepare(ctx context.Context, cfg Config, report na.Report) (*Prepared, error) {
	if abs, err := filepath.Abs(cfg.Root); err == nil {
		cfg.Root = abs
	}
	if abs, err := filepath.Abs(cfg.Output); err == nil {
		cfg.Output = abs
	}
	if cfg.Prefix == "" {
		cfg.Prefix = "live-service"
	}
	naCfg := &na.Config{Root: cfg.Root, Output: cfg.Output, Headless: cfg.Headless, GameID: cfg.GameID}
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := naCfg.UseSaveExpansions(cfg.Save); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
	if err := naCfg.PrepareConfig(); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
	game, err := naCfg.GameSection()
	if err != nil {
		return nil, err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return nil, err
	}
	report["package_files"] = files
	p := &Prepared{cfg: cfg, naCfg: naCfg, StatePath: filepath.Join(cfg.Output, "service.sqlite")}
	p.game, err = na.OpenGame(ctx, naCfg)
	if err != nil {
		return nil, err
	}
	// Any failure below leaves the game to Finish's caller; release the
	// slot either way so a service (or Finish) can take it.
	defer p.Release()
	h := na.NewHarness(p.game.Client, cfg.Output)
	facts, err := na.LoadSave(ctx, h, cfg.Save, report)
	if err != nil {
		return nil, err
	}
	if p.Identity, err = na.ReadIdentity(ctx, h, "identity"); err != nil {
		return nil, err
	}
	report["identity"] = p.Identity
	if cfg.BeforeService != nil {
		if err := cfg.BeforeService(ctx, h, p.Identity, facts); err != nil {
			return nil, fmt.Errorf("before service: %w", err)
		}
	}
	return p, nil
}

// Open reattaches the harness's private bridge session to the game. Only
// one session can hold the GABP slot, so this is valid only while no service
// is running; Release it before the next Start.
func (p *Prepared) Open(ctx context.Context) (*bridge.Client, *na.Harness, error) {
	c, err := p.game.Reattach(ctx)
	if err != nil {
		return nil, nil, err
	}
	return c, na.NewHarness(c, p.cfg.Output), nil
}

// Release closes the harness's session without touching the game, freeing
// the GABP slot for a service.
func (p *Prepared) Release() error {
	return p.game.Release()
}

// SameIdentity reports whether v names the loaded colony, load and map.
func (p *Prepared) SameIdentity(v map[string]any) bool {
	return na.MatchesIdentity(v, p.Identity)
}

// Start launches the service on the shared state path, waits for it to
// attach to the loaded save, acquires player authority and keeps it
// granted until Stop. Every start after the first is a restart against
// the same durable state (report["service_N"]).
func (p *Prepared) Start(ctx context.Context, report na.Report) (*Service, error) {
	var proc *na.ServiceProcess
	var err error
	if p.last == nil {
		spec := na.ServeSpec{Binary: p.cfg.Binary, NativeTimeout: p.cfg.NativeTimeout, Prefix: p.cfg.Prefix, ClockSpeed: p.cfg.ClockSpeed}
		if p.cfg.Families != "" {
			spec.Families = strings.Split(p.cfg.Families, ",")
		}
		if p.cfg.Debug {
			spec.Env = []string{"RIMGOVERNOR_CLOCK_DEBUG=1"}
		}
		proc, err = na.Serve(ctx, p.naCfg, p.game, p.Identity, spec, report)
	} else {
		proc, err = p.last.Restart(ctx)
	}
	if err != nil {
		return nil, err
	}
	if _, err := proc.Acquire(); err != nil {
		proc.Stop()
		return nil, err
	}
	proc.KeepAuthority(ctx)
	p.last = &Service{ServiceProcess: proc}
	return p.last, nil
}

// API calls the service's HTTP API with the player token.
func (s *Service) API(method, path string, body map[string]any) (map[string]any, int, error) {
	return s.ServiceProcess.API(method, path, body, s.Token)
}

// OpenStore opens the service's durable journal for concurrent read-only
// verification; SQLite serves it alongside the running service.
func (p *Prepared) OpenStore(ctx context.Context) (*store.Store, error) {
	return na.OpenStoreWithRetry(ctx, p.StatePath)
}

// Finish ends the hold on the game (games_stop, or the main menu under
// RIMGOVERNOR_ACCEPT_KEEP_GAME, #119) and checks the startup log. Call it
// with no service running; it is safe after a failed run too.
func (p *Prepared) Finish(ctx context.Context, report na.Report) error {
	p.game.Close(report)
	logData, err := os.ReadFile(p.naCfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), p.cfg.Headless)
}
