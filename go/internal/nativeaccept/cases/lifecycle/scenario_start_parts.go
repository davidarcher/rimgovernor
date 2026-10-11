// lifecycle/scenario_start_parts is the native acceptance for the harness's
// ScenarioStart carrying a storyteller override and scenario parts (#2754): a
// one-colonist start under Randy with a forced trait and a scattered thing,
// and the generated save shows each landed. The stamped save a part-bearing
// start leaves for later cases is covered by the nativeaccept unit tests.
package lifecycle

import (
	"context"
	"fmt"
	"os"
	"regexp"
	"strconv"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	scenarioPartsSave        = "scenario-start-parts-2754"
	scenarioPartsStoryteller = "Randy"
	scenarioPartsThing       = "Kibble"
	scenarioPartsCount       = 15
)

func scenarioPartsStart() na.ScenarioStart {
	degree, chance, count := int32(0), float32(1), uint32(scenarioPartsCount)
	start := na.ScenarioStart{
		Scenario: na.DebugScenario, Count: 1, Seed: "scenario-start-parts-2754", Difficulty: na.DebugDifficulty,
		Storyteller: scenarioPartsStoryteller,
		Size:        na.DebugStart{MapSize: na.DefaultMapSize, PlanetCoverage: na.DefaultPlanetCoverage}, SaveName: scenarioPartsSave,
	}
	start.Parts.ForcedTraits = []httpapi.ForcedTraitDTO{{Trait: "Jogger", Degree: &degree, Chance: &chance, Context: "player_starter"}}
	start.Parts.StartingThings = []httpapi.StartingThingDTO{{Placement: "scattered", ThingDef: scenarioPartsThing, Count: &count}}
	return start
}

func init() {
	cases.Register(cases.Case{
		Name: "lifecycle/scenario-start-parts",
		Scope: "Issue #2754: a harness ScenarioStart with Storyteller Randy, a forced Jogger trait and a scattered Kibble stack starts a one-colonist colony; " +
			"the colonist carries the trait, the saved game holds exactly the scattered count and names Randy as the storyteller.",
		Start:  cases.Scenario{Spec: scenarioPartsStart()},
		Quiet:  na.Loud,
		Reason: "the storyteller override is the claim: the quiet swap would replace Randy",
		Budget: 20 * time.Minute,
		Crew:   cases.Crew{Size: 1}, Run: runScenarioStartParts,
	})
}

var scenarioPartsTeller = regexp.MustCompile(`(?s)<storyteller>\s*<def>(\w+)</def>`)

func runScenarioStartParts(ctx context.Context, s cases.Session) error {
	row := map[string]any{}
	s.Report()["scenario_start_parts"] = row
	if err := newColonyPartsTrait(ctx, s.Harness(), row); err != nil {
		return err
	}
	data, err := os.ReadFile(s.Config().ProfileSave(scenarioPartsSave))
	if err != nil {
		return fmt.Errorf("the completed start left no save: %w", err)
	}
	save := string(data)
	total := 0
	for _, m := range partsThingBlock(scenarioPartsThing).FindAllStringSubmatch(save, -1) {
		n := 1
		if c := partsStackCount.FindStringSubmatch(m[1]); c != nil {
			n, _ = strconv.Atoi(c[1])
		}
		total += n
	}
	row["scattered"] = total
	if total != scenarioPartsCount {
		return fmt.Errorf("scattered %s: %d on the map, want %d", scenarioPartsThing, total, scenarioPartsCount)
	}
	teller := scenarioPartsTeller.FindStringSubmatch(save)
	if teller == nil || teller[1] != scenarioPartsStoryteller {
		return fmt.Errorf("the saved storyteller is %v, want %s", teller, scenarioPartsStoryteller)
	}
	row["storyteller"] = teller[1]
	return nil
}
