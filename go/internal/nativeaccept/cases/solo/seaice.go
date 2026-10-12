// Package solo holds the year-long solo acceptance cases of epic #2745: one
// colonist on a very cold ice sheet under Randy Random at the top difficulty
// must survive the RimWorld year, policy.YearDays (60) game days. SoloDaysEnv
// sets a shorter or longer run.
//
// The year is played in ten-day segments. Each segment launches the serve
// process, watches a tick window, stops it and reattaches, and the reattached
// harness then reads what the service's own census cannot: the colonist's
// hediffs and the sleeping temperature and power facts. The segment boundary is
// the probe cadence; a hypothermia or malnutrition excursion that heals inside
// one segment is not seen (the bound is ten days, not a continuous watch).
package solo

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strconv"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/campaign"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const (
	ticksPerDay = 60000
	// ticksPerSecond is the Ultrafast rate measured on the debug colony
	// (nativeaccept.RunSpeed); the budget derives from it. Box load cuts it to
	// about 340, which the suite allowance does not cover.
	ticksPerSecond = 899
	// segmentTicks is one serve launch: ten game days.
	segmentTicks uint64 = 10 * ticksPerDay
	// shelterGraceTicks is how long the sleeping band is not gated: the first
	// segment is the epic's shelter build-out, and the claim is the shelter
	// once it stands. Survival, hypothermia and food are gated from tick zero.
	shelterGraceTicks uint64 = segmentTicks
	// hypothermiaCap is the worst Hypothermia severity a probe may read:
	// serious (0.35), short of the extreme tier where death approaches.
	hypothermiaCap = policy.HypothermiaSerious
	// perSegmentWall is the wall ceiling of one segment's watch; the box load
	// that cuts long runs (~107k ticks historically) is a stall bound, not a
	// reason to shorten the year.
	perSegmentWall = 6 * time.Hour
)

// SoloDaysEnv names the environment variable that sets how many game days the
// solo cases play (default policy.YearDays, the 60-day RimWorld year; a longer
// run such as 365 is set by hand and its budget scales, so the remote plan
// refuses it past the suite allowance).
const SoloDaysEnv = "RIMGOVERNOR_ACCEPT_SOLO_DAYS"

// TargetTicks is the run's length in game ticks.
func TargetTicks() uint64 {
	if raw := os.Getenv(SoloDaysEnv); raw != "" {
		if n, err := strconv.ParseUint(raw, 10, 64); err == nil && n > 0 {
			return n * ticksPerDay
		}
	}
	return policy.YearDays * ticksPerDay
}

// runBudget is the wall clock a run of target ticks needs: ticksPerSecond of
// play plus a launch, stop and probe per segment. The 60-day year is 67
// minutes of play, six segments and the fixed 7 minutes: 92 minutes. The
// remote plan counts the budget once per attempt (max_attempts 2) against the
// 345-minute suite allowance.
func runBudget(target uint64) time.Duration {
	segments := time.Duration((target + segmentTicks - 1) / segmentTicks)
	return time.Duration(target/ticksPerSecond)*time.Second + segments*3*time.Minute + 7*time.Minute
}

// soloStart is the shared one-colonist ice-sheet start: Rhadamant's preset
// shape (Crashlanded, VeryCold, Randy, Extreme) with the parts the variant
// carries.
func soloStart(seed string, parts httpapi.NewColonyPartsDTO) na.ScenarioStart {
	return na.ScenarioStart{
		Scenario: na.DebugScenario, Count: 1, Seed: seed, Difficulty: "Extreme", Storyteller: "Randy",
		Biome: "IceSheet", WorldTemperature: "VeryCold", Parts: parts,
		Size: na.DebugStart{MapSize: 250, PlanetCoverage: 0.3}, SaveName: seed,
	}
}

func init() {
	register("solo/sea-ice-stock", "stock IceSheet variant (existing launcher options only: one colonist, IceSheet, VeryCold, Randy, Extreme, the Crashlanded kit)",
		soloStart("solo-sea-ice-stock-2777", httpapi.NewColonyPartsDTO{}))
	register("solo/sea-ice-rhadamant", "Rhadamant variant (httpapi.RhadamantParts: her kit, forced traits and permanent ColdSnap)",
		soloStart("solo-sea-ice-rhadamant-2777", httpapi.RhadamantParts()))
}

func register(name, variant string, start na.ScenarioStart) {
	cases.Register(cases.Case{
		Name: name,
		Scope: "Issue #2777 (epic #2745): the " + variant + " survives the RimWorld year, 60 game days (" + SoloDaysEnv + " sets another length). At each ten-day segment boundary the colonist is alive, " +
			"has no Hypothermia past serious and no Malnutrition past hungry; after the first segment the sleeping temperature is within the policy's band; " +
			"the census food runway never reaches zero; at the end the colonist lives and power is on. Nightly only.",
		Start: cases.Scenario{Spec: start},
		Keep:  []string{string(na.LiveNeeds)},
		Quiet: na.Loud,
		Serve: &cases.ServeSpec{NativeTimeout: 15 * time.Second, StepStall: 90 * time.Second, Prefix: "solo-sea-ice", PlayerSpeed: "Ultrafast"},
		Reason: "a year of live needs under the scenario's own storyteller (Randy, the top difficulty) is the claim; the quiet swap would replace Randy " +
			"and a short window cannot show a cold year",
		Budget: runBudget(TargetTicks()),
		Crew:   cases.Crew{Size: 1},
		Run:    runYear,
	})
}

func runYear(ctx context.Context, s cases.Session) error {
	report := s.Report()
	var initial []string
	listed, err := na.ListColonists(ctx, s.Harness(), "solo-initial", false, false)
	if err != nil {
		return err
	}
	for _, row := range listed {
		if id := na.RowID(row); id != "" {
			initial = append(initial, id)
		}
	}
	if len(initial) != 1 {
		return fmt.Errorf("a solo start has %d colonists: %v", len(initial), initial)
	}
	report["initial_colonists"] = initial

	var segments []map[string]any
	report["segments"] = segments
	var elapsed uint64
	target := TargetTicks()
	for index := 0; elapsed < target; index++ {
		window := min(segmentTicks, target-elapsed)
		label := fmt.Sprintf("seg%02d", index)
		row, advanced, err := playSegment(ctx, s, label, window, elapsed, elapsed+window >= target, initial)
		segments = append(segments, row)
		report["segments"] = segments
		elapsed += advanced
		report["elapsed_ticks"] = elapsed
		if err != nil {
			return fmt.Errorf("%s (day %d of %d): %w", label, elapsed/ticksPerDay, target/ticksPerDay, err)
		}
	}
	report["survived_days"] = elapsed / ticksPerDay
	return nil
}

// playSegment launches the service for one window, then probes the reattached
// game. It returns the segment's report row and the ticks the watch advanced.
func playSegment(ctx context.Context, s cases.Session, label string, window, before uint64, final bool, initial []string) (map[string]any, uint64, error) {
	row := map[string]any{"label": label, "window": window, "start_elapsed": before}
	spec := s.Spec()
	spec.Prefix = "solo-sea-ice-" + label
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return row, 0, fmt.Errorf("serve: %w", err)
	}
	defer service.Stop()
	watch := na.Report{}
	timeline, watchErr := sustainedfood.Watch(ctx, s.Config(), service, sustainedfood.WatchConfig{
		Watch: perSegmentWall, Window: window, Poll: 10 * time.Second,
		Concern: policy.EnsureFoodSupply, Extra: sustained.ColonyConcerns,
		// A refusal is part of what the year records, never a reason to cut it.
		FailFast: sustainedfood.FailFast{Disabled: true},
	}, watch)
	keep := service.Stop()
	tw, _ := watch["window"].(*sustainedfood.TickWindow)
	var advanced uint64
	if tw != nil {
		advanced = tw.Elapsed
	}
	outcome := sustainedfood.DeriveColonyOutcome(timeline)
	row["samples"], row["advanced"], row["colony"], row["keepalive"] = len(timeline), advanced, outcome, keep
	if watchErr != nil {
		row["error"] = watchErr.Error()
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return row, advanced, errors.Join(watchErr, fmt.Errorf("reattach: %w", err))
	}
	var failures []error
	failures = append(failures, watchErr, auditCensus(outcome, tw, window))
	failures = append(failures, probe(ctx, h, s, label, row, before+advanced, final, initial))
	return row, advanced, errors.Join(failures...)
}

// auditCensus is the in-segment gate over the service's own census: the watch
// reached its window, no colonist was lost and the food runway never reached
// zero.
func auditCensus(outcome sustainedfood.ColonyOutcome, tw *sustainedfood.TickWindow, window uint64) error {
	var failures []error
	if tw == nil || !tw.Reached {
		failures = append(failures, fmt.Errorf("segment window of %d ticks not reached: %+v", window, tw))
	}
	if outcome.ColonistsLost > 0 {
		failures = append(failures, fmt.Errorf("colonist count dropped by %d during the segment", outcome.ColonistsLost))
	}
	if outcome.MinFoodRunwayDays != nil && *outcome.MinFoodRunwayDays <= 0 {
		failures = append(failures, fmt.Errorf("food runway reached %g days", *outcome.MinFoodRunwayDays))
	}
	return errors.Join(failures...)
}

// probe reads the reattached game: the colonist alive without hypothermia past
// the cap or malnutrition past hungry, and the sleeping temperature and power
// facts. It records what it read on row.
func probe(ctx context.Context, h *na.Harness, s cases.Session, label string, row map[string]any, elapsed uint64, final bool, initial []string) error {
	var failures []error
	failures = append(failures, sustained.AuditNutrition(ctx, h, na.Report(row)))
	listed, err := na.ListColonists(ctx, h, label+"-health", true, true)
	if err != nil {
		return errors.Join(append(failures, err)...)
	}
	failures = append(failures, AuditColonists(listed, initial, row))
	facts, err := campaign.ColonyFacts(ctx, h, s, label+"-facts")
	if err != nil {
		return errors.Join(append(failures, err)...)
	}
	f := facts.Facts
	lo, lok := f.SleepingMin.Value()
	hi, hok := f.SleepingMax.Value()
	required, rok := f.PowerRequired.Value()
	headroom, pok := f.PowerHeadroom.Value()
	disabled, dok := f.DisabledConsumers.Value()
	row["sleeping_c"] = map[string]any{"min": lo, "min_known": lok, "max": hi, "max_known": hok}
	row["power"] = map[string]any{"required": required, "required_known": rok, "headroom_w": headroom, "headroom_known": pok, "disabled": disabled, "disabled_known": dok}
	if elapsed > shelterGraceTicks {
		failures = append(failures, auditSleepingBand(lo, lok, hi, hok, policy.DefaultRoundsPolicy()))
	}
	if final {
		failures = append(failures, auditPower(required, rok, headroom, pok, disabled, dok))
	}
	return errors.Join(failures...)
}

// AuditColonists (also the layout/sea-ice-envelope case's probe) holds every initial colonist to alive with Hypothermia at or
// below hypothermiaCap. A corpse can despawn, so a missing pawn fails too.
func AuditColonists(listed []map[string]any, initial []string, row map[string]any) error {
	var failures []error
	alive := map[string]bool{}
	worst := map[string]float64{}
	for _, pawn := range listed {
		id := na.RowID(pawn)
		dead, known := na.AsBool(pawn["dead"])
		alive[id] = known && !dead
		health, _ := na.AsMap(pawn["health"])
		for _, entry := range na.AsSlice(health["hediffs"]) {
			hediff, _ := na.AsMap(entry)
			if na.HediffDef(hediff) == "Hypothermia" {
				worst[id] = max(worst[id], na.AsNumber(hediff["severity"]))
			}
		}
	}
	row["hypothermia"] = worst
	for _, id := range initial {
		switch {
		case !alive[id]:
			failures = append(failures, fmt.Errorf("colonist %s dead or gone", id))
		case worst[id] > hypothermiaCap:
			failures = append(failures, fmt.Errorf("colonist %s Hypothermia %.2f past the %.2f cap", id, worst[id], hypothermiaCap))
		}
	}
	return errors.Join(failures...)
}

// auditSleepingBand holds the sleeping temperatures to the rounds policy's
// foothold band, ColdEnter to HotEnter; an unknown temperature fails.
func auditSleepingBand(lo float64, lok bool, hi float64, hok bool, p policy.RoundsPolicy) error {
	if !lok || !hok {
		return fmt.Errorf("sleeping temperature unknown (min known %v, max known %v)", lok, hok)
	}
	if lo < p.ColdEnter || hi > p.HotEnter {
		return fmt.Errorf("sleeping temperature %.1f..%.1f C outside the %.0f..%.0f C band", lo, hi, p.ColdEnter, p.HotEnter)
	}
	return nil
}

// auditPower is "power on": a consumer exists, none is enabled but unpowered
// and the weakest network has no deficit.
func auditPower(required, rok bool, headroom float64, pok, disabled, dok bool) error {
	switch {
	case !rok || !required:
		return fmt.Errorf("no power consumer at the end of the run (required %v, known %v)", required, rok)
	case !dok || disabled:
		return fmt.Errorf("a consumer is enabled but unpowered (disabled %v, known %v)", disabled, dok)
	case !pok || headroom < 0:
		return fmt.Errorf("power headroom %.0f W (known %v)", headroom, pok)
	}
	return nil
}
