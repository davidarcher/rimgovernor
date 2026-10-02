// Package review holds the colony review run: the governor plays a fresh
// random map for an in-game week while the game records hourly screenshots
// and colony facts for a person (or a model) to read (cmd/colonyreview
// renders the report). It gates nothing.
package review

import (
	"context"
	"hash/fnv"
	"os"
	"path/filepath"
	"strconv"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RecorderTool is the native recorder op (scripts/fixtures/ColonyReviewFixture.cs).
const RecorderTool = "test/colony_review"

// SeedEnv pins the world seed; unset, the seed is the UTC date, so each
// nightly run plays a different map and a rerun the same day repeats it.
const SeedEnv = "RIMGOVERNOR_REVIEW_SEED"

// DaysEnv overrides the in-game length in days (default 7).
const DaysEnv = "RIMGOVERNOR_REVIEW_DAYS"

// biomes are the starts a run picks from by seed: survivable without a
// specialised opening, different enough to show layout and food problems.
var biomes = []string{"TemperateForest", "AridShrubland", "BorealForest", "TropicalRainforest"}

// Seed is SeedEnv or today's UTC date.
func Seed() string {
	if s := os.Getenv(SeedEnv); s != "" {
		return s
	}
	return "review-" + time.Now().UTC().Format("2006-01-02")
}

// Days is DaysEnv or 7.
func Days() uint64 {
	if n, err := strconv.ParseUint(os.Getenv(DaysEnv), 10, 64); err == nil && n > 0 {
		return n
	}
	return 7
}

// Variant is the run's start: Crashlanded's three colonists on a 250 map in
// a biome chosen by the seed.
func Variant(seed string) variantgen.Variant {
	h := fnv.New32a()
	h.Write([]byte(seed))
	return variantgen.Variant{
		Save: "colony-review", Scenario: "Crashlanded", Count: 3, Seed: seed,
		Biome: biomes[h.Sum32()%uint32(len(biomes))], Difficulty: "Medium", MapSize: 250, PlanetCoverage: 0.3,
	}.WithDefaults()
}

func init() {
	seed := Seed()
	v := Variant(seed)
	days := Days()
	cases.Register(cases.Case{
		Name: "review/colony-week",
		Scope: "Review run, not a gate: the governor plays a fresh " + v.Scenario + " map (seed " + seed + ", " + v.Biome +
			") for " + strconv.FormatUint(days, 10) + " in-game days under the storyteller while " + RecorderTool +
			" records an hourly colony screenshot into <output>/review beside the run timeline (colony census and goal states each in-game hour) for cmd/colonyreview. A " +
			"snapshot test cannot cover it: the point is what the colony looks like to a player after a week.",
		Start:       cases.Scenario{Spec: v.Start()},
		Keep:        []string{string(na.LiveNeeds)},
		Quiet:       na.Loud,
		RequiredOps: []string{RecorderTool},
		// The recorder renders through a graphics device; the game is
		// launched for this case alone.
		Graphics: true,
		NoKeep:   true,
		Serve:    &cases.ServeSpec{NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: "colony-review"},
		Reason:   "a week of whole-colony play with the storyteller on is the thing under review",
		Budget:   100 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			dir := filepath.Join(s.Config().Output, "review")
			s.Report()["review_dir"] = dir
			// What map this was, for cmd/colonyreview's header.
			s.Report()["review"] = map[string]any{"seed": seed, "scenario": v.Scenario, "biome": v.Biome,
				"colonists": v.Count, "days": days}
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: 90 * time.Minute, Window: days * 60000, Poll: 30 * time.Second, PollTicks: 2500,
					Goal: policy.EnsureFoodSupply, Extra: sustained.ColonyGoals,
					// A review records whatever happens; nothing ends it early
					// but a stalled clock.
					FailFast:  sustainedfood.FailFast{Disabled: true},
					IdleGrace: -1,
				},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					status, err := h.Call(ctx, "review-start", RecorderTool, map[string]any{"action": "start", "dir": dir})
					report["recorder"] = status
					return err
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					status, err := h.Call(ctx, "review-stop", RecorderTool, map[string]any{"action": "stop"})
					report["recorder"] = status
					return err
				},
			})
			return err
		},
	})
}
