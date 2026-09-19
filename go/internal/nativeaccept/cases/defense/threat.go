package defense

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
)

// defense/threat holds the typed colony facts' threat section (#395) to the
// game's own figures: on the paused baseline the projected raid points and
// wealth split equal what the defense fixture reads from WealthWatcher and
// StorytellerUtility.DefaultThreatPointsNow at the same tick, and after the
// fixture stocks wood and food and recounts, the section reports the larger
// wealth and again the native raid points. The quiet storyteller sets
// difficulty threatScale to 0, so raid points sit on the game's 35-point
// floor throughout: the case proves the projection, not the wealth curve.
// Read-only: no orders, no clock.
func init() {
	cases.Register(cases.Case{
		Name: "defense/threat",
		Scope: "The typed colony read's threat section (raid points, the wealth split and the storyteller wealth) equals the native " +
			"WealthWatcher and DefaultThreatPointsNow figures on the paused " + sustained.BaselineSave + " save, and again after the " +
			"fixture stocks the colony and recounts; read-only (#395).",
		Start:       cases.Save{Name: sustained.BaselineSave},
		RequiredOps: []string{"test/defense_setup"},
		Quiet:       na.QuietRequired,
		Budget:      5 * time.Minute,
		Run:         runThreat,
	})
}

// threatFields pairs the fixture's camelCase keys with the section's.
var threatFields = []string{"raidPoints", "wealthItems", "wealthBuildings", "wealthPawns", "wealthTotal", "storytellerWealth", "adaptationFactor", "difficultyThreatScale"}

func runThreat(ctx context.Context, s cases.Session) error {
	report, h, identity := s.Report(), s.Harness(), s.Identity()
	if !na.Contains(s.Names(), "test/defense_setup") {
		return fmt.Errorf("missing test/defense_setup in discovery; rebuild the native mod with -Fixture DefenseFixture")
	}
	fixture := func(label, op string) (map[string]any, error) {
		out, err := h.Call(ctx, label, "test/defense_setup", map[string]any{"op": op})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(out["success"]); !success {
			return nil, fmt.Errorf("defense_setup %s refused: %#v", op, out)
		}
		threat, ok := na.AsMap(out["threat"])
		if !ok {
			return nil, fmt.Errorf("defense_setup %s carries no threat block: %#v", op, out)
		}
		return threat, nil
	}
	projected := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		section, ok := na.AsMap(observed["threat"])
		if !ok {
			return nil, fmt.Errorf("colony facts carry no threat section")
		}
		_, facts, err := na.Outcome(section, "observed")
		if err != nil {
			return nil, fmt.Errorf("threat section: %w", err)
		}
		for _, field := range threatFields {
			if _, present := facts[field]; !present {
				return nil, fmt.Errorf("threat section lacks %s: %v", field, facts)
			}
		}
		return facts, nil
	}
	// Native floats reach Go as float64 through two different encoders
	// (the fixture's JSON and ProtoJSON), so equality is to float32 precision.
	equal := func(stage string, native, facts map[string]any) error {
		for _, field := range threatFields {
			want, got := na.AsNumber(native[field]), na.AsNumber(facts[field])
			if math.Abs(want-got) > 1e-3*math.Max(1, math.Abs(want)) {
				return fmt.Errorf("%s: %s projected %v, native %v", stage, field, got, want)
			}
		}
		return nil
	}

	baselineNative, err := fixture("inspect-baseline", "inspect")
	if err != nil {
		return err
	}
	baseline, err := projected("colony-facts-baseline")
	if err != nil {
		return err
	}
	report["baseline"] = map[string]any{"native": baselineNative, "projected": baseline}
	if err := equal("baseline", baselineNative, baseline); err != nil {
		return err
	}
	if na.AsNumber(baseline["raidPoints"]) < 35 {
		return fmt.Errorf("baseline raid points %v below the game's 35-point floor", baseline["raidPoints"])
	}
	if _, err := h.Call(ctx, "stock", "test/defense_setup", map[string]any{"op": "stock"}); err != nil {
		return err
	}
	stockedNative, err := fixture("wealth-stocked", "wealth")
	if err != nil {
		return err
	}
	stocked, err := projected("colony-facts-stocked")
	if err != nil {
		return err
	}
	report["stocked"] = map[string]any{"native": stockedNative, "projected": stocked}
	if err := equal("stocked", stockedNative, stocked); err != nil {
		return err
	}
	// 1200 wood and 300 pemmican are worth well over 1000 silver; the
	// recount must show them under items and total alike.
	for _, field := range []string{"wealthItems", "wealthTotal"} {
		if na.AsNumber(stocked[field]) < na.AsNumber(baseline[field])+1000 {
			return fmt.Errorf("%s did not grow with the stocked supplies: %v before, %v after", field, baseline[field], stocked[field])
		}
	}
	if na.AsNumber(stocked["raidPoints"]) < na.AsNumber(baseline["raidPoints"]) {
		return fmt.Errorf("raid points fell as wealth grew: %v before, %v after", baseline["raidPoints"], stocked["raidPoints"])
	}
	return nil
}
