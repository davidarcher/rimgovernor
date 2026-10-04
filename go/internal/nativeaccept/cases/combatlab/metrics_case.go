package combatlab

import (
	"bytes"
	"context"
	"embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
	// pauseModeTool sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs).
	pauseModeTool = "test/letter_pause_mode"
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
			RequiredOps: []string{na.LabStartTool, StageTool, pauseModeTool},
			QuietWorld:  true,
			// A checkpoint capture pauses the served game mid-fight: the
			// external pause revokes authority and drops the fight plan
			// (#890, lab-open at t+2m).
			NoCheckpoint: true,
			Serve:        &cases.ServeSpec{Families: metricsFamilies, PlayerSpeed: metricsSpeed, Prefix: "combatlab"},
			Budget:       cases.LabBudget,
			Run:          func(ctx context.Context, s cases.Session) error { return runMetrics(ctx, s, name) },
		})
	}
}

// runMetrics stages a fixture, serves the planner on it until the tick
// budget is nearly spent, reads it back and writes the reads, the service
// log and the metrics to the bundle.
func runMetrics(ctx context.Context, s cases.Session, name string) error {
	dir := s.Config().Output
	var staged Staged
	var err error
	if probe, _ := Build(name, 0, 0); probe.Arrival != "" {
		staged, err = stagePods(ctx, s, name)
	} else {
		staged, err = Stage(ctx, s.Harness(), name)
	}
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
	if layout := staged.Fixture.Layout; layout != nil {
		record, err := storeLayout(ctx, s.Harness(), s.Identity(), *layout)
		if err != nil {
			return err
		}
		s.Report()["layout"] = record
	}
	// A MajorThreat letter mid-fight (a berserk colonist) pauses the game
	// as the player would; the authority flip then drops the fight plan
	// (#890, lab-open at tick 1057). The metrics measure the fight.
	if reply, err := s.Harness().Call(ctx, "pause-mode-never", pauseModeTool, map[string]any{"mode": "Never"}); err != nil {
		return err
	} else if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", pauseModeTool, reply)
	}
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

// stagePods stages a drop-pod fixture and checks its arrival row is in
// the frame. The native records combat events once a frame capture has
// hooked them (a served game's stream is open long before a raid), so one
// frame is captured before staging. The served run starts with the
// raiders still in their pods: the pending arrival is the threat the
// fight forms on (#908).
func stagePods(ctx context.Context, s cases.Session, name string) (Staged, error) {
	identity, err := typedIdentity(s.Identity())
	if err != nil {
		return Staged{}, err
	}
	frames, err := openCombatFrames(ctx, s.Harness(), identity)
	if err != nil {
		return Staged{}, err
	}
	defer frames.reader.Close()
	if _, err = frames.next(ctx, 0); err != nil {
		return Staged{}, err
	}
	staged, err := Stage(ctx, s.Harness(), name)
	if err != nil {
		return staged, err
	}
	_, tick, err := Tick(ctx, s.Harness(), 1)
	if err != nil {
		return staged, err
	}
	state, err := frames.next(ctx, int64(tick))
	if err != nil {
		return staged, err
	}
	for _, row := range state.Events {
		if bridge.DropPodArrival(row) && int(row.GetOpenTick()) > tick {
			return staged, nil
		}
	}
	return staged, fmt.Errorf("%s: no pending drop-pod arrival row in %d frame events", name, len(state.Events))
}

// serveUntil polls the service's state until the game tick reaches until,
// until the service logs the end of the combat (the colony window after
// it would otherwise run the quiet lab to the budget, #890), or until the
// served clock has sat still for metricsIdle, and returns the last tick
// it saw.
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
		ended := false
		if log, err := os.ReadFile(service.StderrPath()); err == nil {
			ended = bytes.Contains(log, []byte("combat ended"))
		}
		if ended || tick >= until || (tick >= 0 && time.Since(moved) > metricsIdle) {
			return tick, nil
		}
		select {
		case <-ctx.Done():
			return tick, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
}

// storeLayout puts the fixture's layout, as a complete, verified defense
// layout record for the staged world, into the save's governor state: the
// service rebuilds its family tables from the save on the world change
// (#1005), so combat reads it as the colony's standing layout
// (rounds_defense.go, LoadDefenseLayout). A row written into the journal
// before the serve was wiped by that rebuild (#1152).
func storeLayout(ctx context.Context, h *na.Harness, identity map[string]any, l Layout) (store.DefenseLayoutRecord, error) {
	typed, err := typedIdentity(identity)
	if err != nil {
		return store.DefenseLayoutRecord{}, err
	}
	cells := func(in []Cell) []domain.Cell {
		out := make([]domain.Cell, 0, len(in))
		for _, c := range in {
			out = append(out, domain.Cell{X: int32(c.X), Z: int32(c.Z)})
		}
		return out
	}
	record := store.DefenseLayoutRecord{
		World:   store.World{Colony: domain.ColonyID(typed.GetColonyId()), Load: domain.LoadID(typed.GetLoadToken()), Map: domain.MapID(typed.GetMapId())},
		Project: "combatlab-layout",
		Toward:  domain.Rotation(l.Toward),
		Firing:  cells(l.Firing), Retreat: cells(l.Retreat),
		// Validate wants a tier; a fixture's structures are staged, not
		// built by the planner, so its one tier names no buildings.
		Tiers:    []store.DefenseTierRecord{{Name: policy.TierFiringLine, Built: true}},
		Complete: true, Anchored: true,
	}
	if l.Choke != nil {
		// Combat's choke is the trap lane's last cell (#864, #1544).
		record.TrapLane = []domain.Cell{{X: int32(l.Choke.X), Z: int32(l.Choke.Z)}}
		record.Chokepoint = record.TrapLane[0]
	}
	if err = record.Validate(); err != nil {
		return record, err
	}
	data, err := json.Marshal(record)
	if err != nil {
		return record, err
	}
	blob, err := json.Marshal(store.GovernorFamilyBlob{SchemaVersion: store.GovernorStateSchemaVersion, Record: data})
	if err != nil {
		return record, err
	}
	reply, err := h.Wire(ctx, "store-layout", "lifecycle_put_governor_state", map[string]any{"key": store.GovernorDefenseLayoutKey, "blob": string(blob)})
	if err != nil {
		return record, err
	}
	if _, _, err = na.Outcome(reply, "loaded"); err != nil {
		return record, fmt.Errorf("store-layout: %w", err)
	}
	return record, nil
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
