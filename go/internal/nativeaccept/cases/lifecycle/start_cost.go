// tools/startcost measures what a new-colony start costs and proves a pinned
// spec reproduces (#2029, epic #2019). It is a diagnostic no tier runs
// (the tools/ prefix): each start needs a fresh main menu, so every row opens
// and retires its own game, and a failed row is recorded and the sweep goes on
// (a failed start leaves game state, the next row has a new process).
//
// Rows: the planet coverage x map size grid on the tribal-8 world rules (eight
// LostTribe colonists), a colonist-count sweep and a seed sweep for the team
// policy's reroll cost, and the tribal-8 spec twice for reproducibility
// (colonist names, traits, skills, tile id and a map digest of the saved
// terrain grid and thing positions).
package lifecycle

import (
	"context"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "tools/startcost",
		Scope: "Diagnostic (#2029): start time by planet coverage and map size, team-policy reroll cost by colonist count and seed, " +
			"and the tribal-8 spec started twice giving the same colonists, tile and map digest. Reported under start_cost; gates nothing.",
		Start:  cases.Owned{},
		Reason: "each start needs a fresh main menu process, so every row opens and retires its own game",
		NoKeep: true,
		Budget: 90 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runStartCost,
	})
}

// startCostSpec is the tribal-8 rules at a size: LostTribe, quiet, the
// baseline's biome preference widened so a small planet still offers a tile.
func startCostSpec(seed string, count, mapSize int, coverage float64, saveName string) map[string]any {
	return map[string]any{
		"scenario": "LostTribe", "colonistCount": count, "seed": seed,
		"biomes":     []any{"TemperateForest", "BorealForest", "TemperateSwamp", "AridShrubland", "Tundra"},
		"difficulty": "Medium", "storyteller": na.QuietStorytellerDef, "worldTemperature": "LittleBitColder",
		"mapSize": mapSize, "planetCoverage": coverage, "saveName": saveName,
	}
}

type startCostRead struct {
	team *teamRead
	tile int
	hash string
}

func (r *startCostRead) digest() string {
	return fmt.Sprintf("tile=%d map=%s team=%s", r.tile, r.hash, r.team.digest())
}

func runStartCost(ctx context.Context, s cases.Session) error {
	cfg := s.Config()
	rows := map[string]any{}
	var failures []string
	s.Report()["start_cost"] = rows
	// core runs on a Core-only profile, as the baseline is generated.
	runOn := func(cfg *na.Config, name string, spec map[string]any) *startCostRead {
		row := map[string]any{"spec": spec, "expansions": cfg.Expansions}
		rows[name] = row
		read, err := startCostRow(ctx, cfg, name, spec, row)
		if err != nil {
			row["error"] = err.Error()
			return nil
		}
		return read
	}

	run := func(name string, spec map[string]any) *startCostRead { return runOn(cfg, name, spec) }
	coreCfg := *cfg
	coreCfg.Expansions = []string{}
	// RIMGOVERNOR_STARTCOST_ONLY=repro skips the sweep and runs only the
	// reproducibility starts.
	sweep := os.Getenv("RIMGOVERNOR_STARTCOST_ONLY") != "repro"
	for _, coverage := range []float64{0.05, 0.3, 0.5, 1.0} {
		if !sweep {
			break
		}
		for _, size := range []int{150, 250, 350} {
			name := fmt.Sprintf("grid-cov%.2f-size%d", coverage, size)
			run(name, startCostSpec("startcost-grid", 8, size, coverage, name))
		}
	}
	// Reroll cost by colonist count, then by seed, on the small world so the
	// rolling phase dominates.
	for _, count := range []int{3, 8, 10} {
		if !sweep {
			break
		}
		name := fmt.Sprintf("count-%d", count)
		run(name, startCostSpec("startcost-count", count, 150, 0.05, name))
	}
	for i := 1; i <= 6 && sweep; i++ {
		name := fmt.Sprintf("seed-%d", i)
		run(name, startCostSpec(fmt.Sprintf("startcost-seed-%d", i), 8, 150, 0.05, name))
	}

	// Reproducibility: the baseline spec and two more seeds, each twice under
	// one save name, on Core-only and on all DLC.
	for _, profile := range []struct {
		name string
		cfg  *na.Config
	}{{"core", &coreCfg}, {"dlc", cfg}} {
		// The profile on disk is shared, so each profile writes its own
		// ModsConfig before its rows run (core rows first, then all DLC).
		if err := profile.cfg.PrepareConfig(); err != nil {
			return fmt.Errorf("prepare the %s profile: %w", profile.name, err)
		}
		for i, seed := range []string{na.BaselineStart.Seed, "startcost-repro-2", "startcost-repro-3"} {
			tag := fmt.Sprintf("%s-s%d", profile.name, i+1)
			spec := func(save string) map[string]any {
				return startCostSpec(seed, na.BaselineStart.Count, na.BaselineStart.Size.MapSize, na.BaselineStart.Size.PlanetCoverage, save)
			}
			first := runOn(profile.cfg, "repro-"+tag+"-first", spec("startcost-repro-"+tag+"-a"))
			second := runOn(profile.cfg, "repro-"+tag+"-second", spec("startcost-repro-"+tag+"-b"))
			if first == nil || second == nil {
				return fmt.Errorf("the %s reproducibility starts did not both finish (see start_cost)", tag)
			}
			teamSame := first.team.digest() == second.team.digest() && first.tile == second.tile
			rows["repro_"+tag+"_same"] = map[string]any{"colonists_and_tile": teamSame, "map": first.hash == second.hash}
			if first.digest() != second.digest() {
				failures = append(failures, fmt.Sprintf("%s: the same spec twice gave different colonies (colonists+tile same: %v, map same: %v)", tag, teamSame, first.hash == second.hash))
			}
		}
	}
	if len(failures) > 0 {
		return fmt.Errorf("%s", strings.Join(failures, "; "))
	}
	return nil
}

// startCostRow opens a game at the main menu, times one start of spec and
// reads what a pinned spec must reproduce.
func startCostRow(ctx context.Context, cfg *na.Config, name string, spec map[string]any, row map[string]any) (*startCostRead, error) {
	reuse, err := na.OpenReusableGame(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", name, err)
	}
	defer func() {
		retireCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = reuse.Retire(retireCtx, name+" row complete")
	}()
	h, err := reuse.Session(ctx)
	if err != nil {
		return nil, err
	}
	request := na.NewColonyRequest(fmt.Sprintf("startcost-%s-%d", name, time.Now().UnixNano()), spec, 25*time.Minute)
	run, err := na.RunNewColony(ctx, h, request, name)
	row["phases_ms"], row["rerolls"], row["total_ms"] = run.PhaseMs, run.Rerolls, run.TotalMs
	if err != nil {
		return nil, err
	}
	if rolled, ok := run.PhaseMs["ROLLING_COLONISTS"]; ok && run.Rerolls > 0 {
		if next, ok := phaseAfter(run, "ROLLING_COLONISTS"); ok && next > rolled {
			row["ms_per_reroll"] = float64(next-rolled) / float64(run.Rerolls)
		}
	}

	count := int(na.AsNumber(spec["colonistCount"]))
	team, err := readTeam(ctx, h, name, count)
	if err != nil {
		return nil, err
	}
	read := &startCostRead{team: team}
	if hash, err := na.SaveMapHash(cfg.ProfileSave(na.AsString(spec["saveName"]))); err != nil {
		return nil, err
	} else {
		read.hash = hash
	}
	if read.tile, err = na.SaveStartingTile(cfg.ProfileSave(na.AsString(spec["saveName"]))); err != nil {
		return nil, err
	}
	row["tile"] = read.tile
	row["map_digest"], row["team"] = read.hash, strings.Split(team.digest(), ";")
	return read, nil
}

// phaseAfter is the start of the phase following name in wire order, when the
// poll saw it.
func phaseAfter(run na.NewColonyRun, name string) (int64, bool) {
	seen := false
	for _, phase := range na.NewColonyPhaseOrder {
		if seen {
			if ms, ok := run.PhaseMs[phase]; ok {
				return ms, true
			}
			continue
		}
		seen = phase == name
	}
	return 0, false
}
