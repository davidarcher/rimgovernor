package combatlab

import (
	"context"
	"embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	// metricsTicks is a fixture run's game-tick budget (#845 ground rule 3).
	metricsTicks = 5000
	// metricsChunk is one synchronous tick call between reads.
	metricsChunk = 250
)

// Baselines are each fixture's metrics recorded from main before any
// Phase 1 tactic (#855): a tactic child reports its delta against them.
//
//go:embed baselines/*.json
var baselines embed.FS

// Baseline returns the committed baseline metrics for a fixture.
func Baseline(name string) (Metrics, error) {
	var m Metrics
	data, err := baselines.ReadFile("baselines/" + name + ".json")
	if err != nil {
		return m, err
	}
	return m, json.Unmarshal(data, &m)
}

func init() {
	for _, name := range Names {
		probe, _ := Build(name, 0, 0)
		cases.Register(cases.Case{
			Name:        "combatlab/metrics-" + name,
			Scope:       fmt.Sprintf("Combat metrics (#855): stage %s and run it up to %d game ticks or until one side is down, recording every read to %s and the aggregate (damage, downs, deaths, fled, contact-to-resolution, friendly fire, orders, step latency) to %s. Evidence, not a gate.", name, metricsTicks, ReadsFile, MetricsFile),
			Start:       cases.Lab{Colonists: probe.Colonists},
			RequiredOps: []string{na.LabStartTool, StageTool},
			QuietWorld:  true,
			Budget:      cases.LabBudget,
			Run:         func(ctx context.Context, s cases.Session) error { return runMetrics(ctx, s, name) },
		})
	}
}

// RunFixture stages a fixture, ticks it to resolution or the tick budget,
// and writes the reads and metrics under dir.
func RunFixture(ctx context.Context, h *na.Harness, name, dir string) (Metrics, error) {
	staged, err := Stage(ctx, h, name)
	if err != nil {
		return Metrics{}, err
	}
	sides := StagedSides(staged)
	first, err := rawRead(ctx, h, map[string]any{"action": "read"})
	if err != nil {
		return Metrics{}, err
	}
	first["staged"] = sides
	reads := []map[string]any{first}
	start := int(na.AsNumber(first["tick"]))
	for ran := 0; ran < metricsTicks; ran += metricsChunk {
		read, err := rawRead(ctx, h, map[string]any{"action": "tick", "ticks": metricsChunk})
		if err != nil {
			return Metrics{}, err
		}
		reads = append(reads, read)
		if Aggregate(name, sides, reads).ResolvedTick >= 0 {
			break
		}
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return Metrics{}, err
	}
	f, err := os.Create(filepath.Join(dir, ReadsFile))
	if err != nil {
		return Metrics{}, err
	}
	enc := json.NewEncoder(f)
	for _, r := range reads {
		if err := enc.Encode(r); err != nil {
			f.Close()
			return Metrics{}, err
		}
	}
	if err := f.Close(); err != nil {
		return Metrics{}, err
	}
	m, err := AggregateBundle(dir, name)
	if err != nil {
		return m, err
	}
	if m.Ticks > metricsTicks {
		return m, fmt.Errorf("%s ran %d ticks from %d, over the %d-tick budget", name, m.Ticks, start, metricsTicks)
	}
	data, _ := json.MarshalIndent(m, "", "  ")
	return m, os.WriteFile(filepath.Join(dir, MetricsFile), append(data, '\n'), 0o644)
}

func runMetrics(ctx context.Context, s cases.Session, name string) error {
	m, err := RunFixture(ctx, s.Harness(), name, s.Config().Output)
	if err != nil {
		return err
	}
	s.Report()["metrics"] = m
	if base, err := Baseline(name); err == nil {
		s.Report()["baseline"] = base
	}
	return nil
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
