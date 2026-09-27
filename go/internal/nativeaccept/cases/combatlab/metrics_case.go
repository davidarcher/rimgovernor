package combatlab

import (
	"context"
	"embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	// metricsTicks is a fixture run's game-tick budget (#845 ground rule 3).
	metricsTicks = 5000
	// metricsSpeed is the player speed the served clock follows (#875):
	// Fast keeps the fight well inside the case budget while the
	// service's 1 s state refresh lags the game by a few hundred ticks.
	metricsSpeed = "Fast"
	// metricsStopMargin is how far short of the budget the service is
	// stopped: the state refresh lag plus the reattach.
	metricsStopMargin = 800
	// metricsIdle is how long a still served clock means the run is over.
	metricsIdle = 20 * time.Second
)

// metricsFamilies is today's autopilot answer to a fight: the routine
// defense planner (hold-the-line / squad defense), with the rescue and
// tend families it hands downed colonists to.
var metricsFamilies = []string{"defense", "tend", "rescue"}

// Baselines are each fixture's metrics recorded from main before any
// Phase 1 tactic (#855), with the routine defense planner serving the run:
// a tactic child reports its delta against them.
//
//go:embed baselines/*.json
var baselines embed.FS

// Baseline returns the committed baseline metrics for a fixture, refusing
// one recorded under another schema version.
func Baseline(name string) (Metrics, error) {
	var m Metrics
	data, err := baselines.ReadFile("baselines/" + name + ".json")
	if err != nil {
		return m, err
	}
	if err := json.Unmarshal(data, &m); err != nil {
		return m, err
	}
	if m.Version != MetricsVersion {
		return m, fmt.Errorf("baseline %s is metrics schema version %d, this build reads version %d: re-record it with combatlab/metrics-%s", name, m.Version, MetricsVersion, name)
	}
	return m, nil
}

func init() {
	for _, name := range Names {
		probe, _ := Build(name, 0, 0)
		cases.Register(cases.Case{
			Name:        "combatlab/metrics-" + name,
			Scope:       fmt.Sprintf("Combat metrics (#855, #869): stage %s, serve the routine defense planner on it for up to %d game ticks, and record the reads before and after to %s and the aggregate (damage, downs, deaths, fled, contact-to-resolution, friendly fire, orders, step latency) to %s. Evidence, not a gate: native because the metrics are vanilla combat outcomes under the live planner.", name, metricsTicks, ReadsFile, MetricsFile),
			Start:       cases.Lab{Colonists: probe.Colonists},
			RequiredOps: []string{na.LabStartTool, StageTool},
			QuietWorld:  true,
			Serve:       &cases.ServeSpec{Families: metricsFamilies, PlayerSpeed: metricsSpeed, Prefix: "combatlab"},
			Budget:      cases.LabBudget,
			Run:         func(ctx context.Context, s cases.Session) error { return runMetrics(ctx, s, name) },
		})
	}
}

// runMetrics stages a fixture, serves the planner on it until the tick
// budget is nearly spent, reads it back and writes the reads, the service
// log and the metrics to the bundle.
func runMetrics(ctx context.Context, s cases.Session, name string) error {
	dir := s.Config().Output
	staged, err := Stage(ctx, s.Harness(), name)
	if err != nil {
		return err
	}
	sides := StagedSides(staged)
	first, err := rawRead(ctx, s.Harness(), map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	first["staged"] = sides
	start := int(na.AsNumber(first["tick"]))
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	served, err := serveUntil(ctx, service, start+metricsTicks-metricsStopMargin)
	service.Stop()
	s.Report()["servedTick"] = served
	if err != nil {
		return err
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	last, err := rawRead(ctx, h, map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	if err := writeReads(dir, []map[string]any{first, last}); err != nil {
		return err
	}
	if log, err := os.ReadFile(service.StderrPath()); err == nil {
		if err := os.WriteFile(filepath.Join(dir, ServiceLogFile), log, 0o644); err != nil {
			return err
		}
	}
	m, err := AggregateBundle(dir, name)
	if err != nil {
		return err
	}
	data, _ := json.MarshalIndent(m, "", "  ")
	if err := os.WriteFile(filepath.Join(dir, MetricsFile), append(data, '\n'), 0o644); err != nil {
		return err
	}
	s.Report()["metrics"] = m
	if base, err := Baseline(name); err == nil {
		s.Report()["baseline"] = base
	}
	if m.Ticks > metricsTicks {
		return fmt.Errorf("%s ran %d ticks from %d, over the %d-tick budget", name, m.Ticks, start, metricsTicks)
	}
	return nil
}

// serveUntil polls the service's state until the game tick reaches until,
// or until the served clock has sat still for metricsIdle (the fight is
// over and the quiet world leaves the planner no work to admit), and
// returns the last tick it saw.
func serveUntil(ctx context.Context, service *na.ServiceProcess, until int) (int, error) {
	tick, moved := -1, time.Now()
	for {
		if err := service.Exited(); err != nil {
			return tick, fmt.Errorf("service exited at tick %d: %w", tick, err)
		}
		if st, status, err := service.API("GET", "/api/state", nil, ""); err == nil && status == 200 {
			game, _ := na.AsMap(st["game"])
			if t, ok := game["tick"]; ok && t != nil {
				if n := int(na.AsNumber(t)); n != tick {
					tick, moved = n, time.Now()
				}
			}
		}
		if tick >= until || (tick >= 0 && time.Since(moved) > metricsIdle) {
			return tick, nil
		}
		select {
		case <-ctx.Done():
			return tick, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
}

func writeReads(dir string, reads []map[string]any) error {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	f, err := os.Create(filepath.Join(dir, ReadsFile))
	if err != nil {
		return err
	}
	enc := json.NewEncoder(f)
	for _, r := range reads {
		if err := enc.Encode(r); err != nil {
			f.Close()
			return err
		}
	}
	return f.Close()
}

func rawRead(ctx context.Context, h *na.Harness, args map[string]any) (map[string]any, error) {
	reply, err := h.Call(ctx, "combatlab-metrics-"+na.AsString(args["action"]), StageTool, args)
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s %v refused: %#v", StageTool, args["action"], reply)
	}
	return reply, nil
}
