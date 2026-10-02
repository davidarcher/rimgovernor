package tools

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

// Baseline is the committed tribal8 baseline's generation spec (#192). It
// is generated under the run's default profile, every installed DLC since
// #1260; copy root/profile/Saves/<Save>.rws over
// scripts/fixtures/saves after auditing it.
var Baseline = variantgen.Variant{
	Save: sustained.BaselineSave, Scenario: "LostTribe", Count: 8, Seed: "rimgovernor-tribal-eight-e",
	Biome: "TemperateForest", Difficulty: "Medium", WorldTemperature: "LittleBitColder",
	MapSize: 250, PlanetCoverage: 0.3,
}.WithDefaults()

func init() {
	cases.Register(cases.Case{
		Name: "tools/baselinegen",
		Scope: "Fixture generation: regenerates " + sustained.BaselineSave + " (LostTribe, eight colonists, seed " +
			Baseline.Seed + ") under the run's profile; requires a ScenarioStartFixture build.",
		Start:  cases.Scenario{Spec: Baseline.Start()},
		Budget: 8 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			row := map[string]any{"variant": Baseline}
			s.Report()["generated"] = row
			return variantgen.SaveVariant(ctx, s.Harness(), s.Config().Root, s.Config().Headless, Baseline.Save, row)
		},
	})
}
