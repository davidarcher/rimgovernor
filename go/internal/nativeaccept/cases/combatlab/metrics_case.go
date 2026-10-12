package combatlab

import (
	"context"
	"embed"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"os"
	"path/filepath"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// metricsTicks is a fixture run's game-tick budget .
	metricsTicks = 10000
	// metricsSpeed labels the response evidence. The served clock runs
	// Ultrafast uncapped like every acceptance case, so the service's 1 s
	// state refresh lags the game by over a thousand ticks.
	metricsSpeed = "Ultrafast"
	// metricsStopMargin is how far short of the budget the service is
	// stopped: the state refresh lag plus the reattach.
	metricsStopMargin = 2500
	// metricsIdle is how long a still served clock means the run is over. A
	// cold service on a loaded box spends 20 s or more on its first frame
	// capture and definition catalog read before its first round (#2843), so
	// 20 s ended runs at tick 0 with no orders.
	metricsIdle = 90 * time.Second
	// pauseModeTool sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs).
	pauseModeTool = "test/letter_pause_mode"
)

// smokeOnly marks lab-open's metrics: the fixture is the smoke scaffold for the
// clock, orders and recorder (#854), not a tactics benchmark, and its outcome
// swings run to run. Read its baseline as "the pipeline ran a fight", and judge
// tactics on the structured fixtures (choke, ranged, breach, ...).
const smokeOnly = " Smoke only: lab-open is an unstructured 3v3 whose outcome is not a tactics signal."

// metricsFamilies is today's autopilot answer to a fight: the routine
// defense planner (hold-the-line / squad defense), with the rescue and
// tend families it hands downed colonists to.
var metricsFamilies = []routinefamily.Family{routinefamily.Defense, routinefamily.Tend, routinefamily.Rescue}

// BaselineSeeds is the seed count a baseline sweep records (seeds 1..N) and
// `acceptance sweep` runs by default.
const BaselineSeeds = 20

// BaselinesDir is the baselines directory relative to the Go module root.
const BaselinesDir = "internal/nativeaccept/cases/combatlab/baselines"

// Baselines are each fixture's sweep (per-seed rows plus summary) recorded
// from main with the routine defense planner serving the run: a tactic
// child sweeps the same seeds and pairs its rows against them.
// `acceptance sweep <fixture> -record` rewrites one.
//
//go:embed baselines/*.json
var baselines embed.FS

// Baseline returns the committed baseline sweep for a fixture, refusing
// one recorded under another schema version.
func Baseline(name string) (combatmetrics.Sweep, error) {
	data, err := baselines.ReadFile("baselines/" + name + ".json")
	if err != nil {
		return combatmetrics.Sweep{}, err
	}
	s, err := combatmetrics.ParseSweep(data)
	if err != nil {
		return s, fmt.Errorf("baseline %s: %w; re-record it with `acceptance sweep %s -record`", name, err, name)
	}
	return s, nil
}

func init() {
	for _, name := range Names {
		probe, _ := Build(name, 0, 0)
		scope := fmt.Sprintf("Combat metrics (#855, #869): stage %s, serve the routine defense planner on it for up to %d game ticks, and record the reads before and after to %s and the aggregate (damage, downs, deaths, fled, contact-to-resolution, friendly fire, orders, step latency) to %s, with the shot tap (launches, impacts, jobs, stances, think ticks) to %s. Evidence, not a gate: native because the metrics are vanilla combat outcomes under the live planner.", name, metricsTicks, ReadsFile, MetricsFile, ShotsFile)
		if name == "lab-open" {
			scope += smokeOnly
		}
		cases.Register(cases.Case{
			Name:        "combatlab/metrics-" + name,
			Scope:       scope,
			Start:       cases.Lab{Colonists: probe.Colonists},
			RequiredOps: []string{na.LabStartTool, StageTool, pauseModeTool},
			QuietWorld:  false,
			// A ring capture pauses the served game mid-fight: the
			// external pause revokes authority and drops the fight plan
			// (lab-open at t+2m). The stage bundle is taken right after
			// staging, before the planner is served (#2837), so a run
			// loads it instead of booting and staging.
			NoCheckpoint: true,
			Stages:       []string{metricsStage},
			Serve:        &cases.ServeSpec{Families: metricsFamilies, Prefix: "combatlab"},
			Budget:       cases.LabBudget,
			Crew:         cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error { return runMetrics(ctx, s, name) },
		})
	}
}

const (
	// metricsStage is the declared stage whose bundle a later run opens on.
	metricsStage = "staged"
	// metricsStagedKey is the checkpoint state key of the stagedRecord.
	metricsStagedKey = "combatlab_staged"
)

// stagedRecord is what the code after the stage reads of the staging: the
// pawn sides, the lab centre the fixture was built around (its layout cells
// are rebuilt from it) and the digest and seed the staging read back.
type stagedRecord struct {
	Sides  map[string]string
	Center Cell
	Digest string
	Seed   int
}

// runMetrics stages a fixture, serves the planner on it until the tick
// budget is nearly spent, reads it back and writes the reads, the service
// log and the metrics to the bundle.
func runMetrics(ctx context.Context, s cases.Session, name string) error {
	dir := s.Config().Output
	// The staging is the stage: the save is captured right after it, before
	// the planner is served, so a later run loads it instead of booting and
	// staging again. What the code after the stage needs travels in the
	// stage's checkpoint state.
	var record stagedRecord
	if err := s.Stage(ctx, metricsStage, func(ctx context.Context) error {
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
		record = stagedRecord{Sides: StagedSides(staged), Center: staged.Center, Digest: staged.Digest, Seed: staged.Seed}
		na.SetCheckpointState(metricsStagedKey, record)
		return nil
	}); err != nil {
		return err
	}
	resumed := false
	if restored := cases.RestoredState(s, metricsStagedKey); restored != nil {
		data, err := json.Marshal(restored)
		if err != nil {
			return err
		}
		if err := json.Unmarshal(data, &record); err != nil {
			return fmt.Errorf("staged record of %s: %w", name, err)
		}
		resumed = true
	}
	if record.Sides == nil {
		return fmt.Errorf("%s: no staged record after the stage", name)
	}
	s.Report()["stage_resumed"] = resumed
	s.Report()["staged_digest"] = record.Digest
	s.Report()["staged_seed"] = record.Seed
	sides := record.Sides
	first, err := rawRead(ctx, s.Harness(), map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	first["staged"] = sides
	start := int(na.AsNumber(first["tick"]))
	if fixture, _ := Build(name, record.Center.X, record.Center.Z); fixture.Layout != nil {
		layout := fixture.Layout
		record, err := storeLayout(ctx, s.Harness(), s.Identity(), *layout)
		if err != nil {
			return err
		}
		s.Report()["layout"] = record
	}
	// A MajorThreat letter mid-fight (a berserk colonist) pauses the game
	// as the player would; the authority flip then drops the fight plan
	// (lab-open at tick 1057). The metrics measure the fight.
	if reply, err := s.Harness().Call(ctx, "pause-mode-never", pauseModeTool, map[string]any{"mode": "Never"}); err != nil {
		return err
	} else if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", pauseModeTool, reply)
	}
	// A run seed reseeds the RNG on the paused, loaded checkpoint, right
	// before the planner is served: nothing draws between (#2849).
	if seed := s.RunSeed(); seed != 0 {
		draws, err := Reseed(ctx, s.Harness(), seed)
		if err != nil {
			return err
		}
		s.Report()["run_seed"] = seed
		s.Report()["run_seed_draws"] = draws
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
	logPath := na.ServiceLogsPath(s.Config().Output)
	served, err := serveUntil(ctx, service, logPath, start+metricsTicks-metricsStopMargin)
	service.Stop()
	s.Report()["servedTick"] = served
	if err != nil {
		return err
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-after", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	last, err := rawRead(ctx, h, map[string]any{"action": "read"})
	if err != nil {
		return err
	}
	if err := writeReads(dir, []map[string]any{first, last}); err != nil {
		return err
	}
	shots, err := rawRead(ctx, h, map[string]any{"action": "shots"})
	if err != nil {
		return err
	}
	if err := writeShots(dir, shots); err != nil {
		return err
	}
	if err := writeCombatRows(logPath, filepath.Join(dir, CombatRowsFile)); err != nil {
		return err
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
	response, err := na.NewResponseEvidence(name, metricsSpeed, int64(start))
	if err != nil {
		return err
	}
	if err := response.ReadLogs(logPath); err != nil {
		return err
	}
	for _, value := range na.AsSlice(last["damage"]) {
		hit, _ := na.AsMap(value)
		if na.AsString(hit["instigatorSide"]) == Colonist && na.AsString(hit["victimSide"]) == Hostile && na.AsNumber(hit["dealt"]) > 0 {
			response.ObserveEffect(int64(na.AsNumber(hit["tick"])), "colonist_damaged_hostile")
		}
	}
	s.Report()["response"] = response
	if base, err := Baseline(name); err == nil {
		s.Report()["baseline"] = base.Summary
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
// fight forms on.
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
// until the service records the end of the combat (a combat_summary row at
// the log; the colony window after it would otherwise run the quiet lab to
// the budget), or until the served clock has sat still for
// metricsIdle, and returns the last tick it saw.
func serveUntil(ctx context.Context, service *na.ServiceProcess, logPath string, until int) (int, error) {
	tick, moved := -1, time.Now()
	tail := na.NewLogTailFromStart(logPath)
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
		rows, err := tail.Next()
		if err != nil {
			return tick, err
		}
		if slices.ContainsFunc(rows, combatEnded) || tick >= until || (tick >= 0 && time.Since(moved) > metricsIdle) {
			return tick, nil
		}
		select {
		case <-ctx.Done():
			return tick, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
}

// combatEnded reports whether a row is the end-of-combat summary.
func combatEnded(row na.LogRow) bool {
	return row.Kind == "combat_summary"
}

// writeCombatRows copies the combat rows ScanLogs reads from the rotated
// segments (oldest first) and the live file of the service's OTLP log ring
// into the bundle, one OTLP line each (the bundle is itself a log ring that
// ScanLogs reads back).
func writeCombatRows(from, to string) error {
	var kept []byte
	err := otel.ReadLines(from, func(line []byte) error {
		if row, ok := na.ParseLogRow(line); ok && CombatRow(row) {
			kept = append(append(kept, line...), 0x0a)
		}
		return nil
	})
	if err != nil {
		return err
	}
	if len(kept) == 0 {
		return nil
	}
	return os.WriteFile(to, kept, 0o644)
}

// storeLayout puts the fixture's layout, as a complete, verified defense
// layout record for the staged world, into the save's governor state: the
// service rebuilds its family tables from the save on the world change,
// so combat reads it as the colony's standing layout
// (rounds_defense.go, LoadDefenseLayout). A row written into the journal
// before the serve was wiped by that rebuild.
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
		World:    store.World{Colony: domain.ColonyID(typed.GetColonyId()), Load: domain.LoadID(typed.GetLoadToken()), Map: domain.MapID(typed.GetMapId())},
		Standard: "combatlab-layout",
		Firing:   cells(l.Firing), Retreat: cells(l.Retreat),
		// Validate wants a tier; a fixture's structures are staged, not
		// built by the planner, so its one tier names no buildings.
		Tiers:    []store.DefenseTierRecord{{Name: policy.TierFiringLine, Built: true}},
		Complete: true,
	}
	if l.Choke != nil {
		// Combat's choke is the trap lane's last cell.
		record.TrapLane = []domain.Cell{{X: int32(l.Choke.X), Z: int32(l.Choke.Z)}}
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
	// The batch put replaces the whole set, so read the save's blobs and add the
	// layout to them.
	read, err := h.Wire(ctx, "store-layout-read", "lifecycle_read_governor_state", map[string]any{})
	if err != nil {
		return record, err
	}
	_, loaded, err := na.Outcome(read, "loaded")
	if err != nil {
		return record, fmt.Errorf("store-layout-read: %w", err)
	}
	blobs := map[string]string{}
	saved, _ := na.AsMap(loaded["blobs"])
	for key, value := range saved {
		blobs[key] = na.AsString(value)
	}
	blobs[store.GovernorDefenseLayoutKey] = string(blob)
	// Combat reads the direction toward home from the plan's killbox opening,
	// so the staged world holds a plan whose only content is that opening.
	plan, err := json.Marshal(policy.OpeningPlan(domain.Rotation(l.Toward)))
	if err != nil {
		return record, err
	}
	scope := store.GovernorScope{Colony: record.World.Colony, Map: record.World.Map}
	planBlob, err := json.Marshal(store.GovernorFamilyBlob{SchemaVersion: store.GovernorStateSchemaVersion, Scope: &scope, Record: plan})
	if err != nil {
		return record, err
	}
	blobs[store.GovernorLayoutPlanKey] = string(planBlob)
	reply, err := h.Wire(ctx, "store-layout", "lifecycle_put_governor_state_batch", map[string]any{"blobs": blobs})
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
