// The waste/colony_facts_equality case is the permanent ColonyFacts
// equality check (#1296): on the waste fixture (item, corpse and waste rows
// whose order matters, and ResourceSources, #1295) the native snapshot read with every read
// optimization off and then on must be byte-identical.
package waste

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "waste/colony_facts_equality",
		Scope: "ColonyFacts read optimizations change no byte: the snapshot with optimizations off and on " +
			"is identical, row order included, on a map with waste rows.",
		Start:      cases.Fixture{Op: "test/waste_fixture", Args: map[string]any{"burial": false}, On: cases.LabStart()},
		QuietWorld: true,
		Budget:     3 * time.Minute,
		Crew:       cases.Crew{Size: 3}, Run: runEquality,
	})
}

func runEquality(ctx context.Context, s cases.Session) error {
	// A roofed granite block with a face and a buried steel deposit gives
	// the ResourceSources comparison mine, buried and roof-support rows (#1295).
	if _, err := s.Harness().Call(ctx, "buried-steel", "test/buried_steel", map[string]any{"action": "prepare"}); err != nil {
		return err
	}
	result, err := s.Harness().Call(ctx, "equality", "test/colony_facts_equality", map[string]any{})
	if err != nil {
		return err
	}
	for key, value := range result {
		s.Report()[key] = value
	}
	if equal, _ := result["equal"].(bool); !equal {
		return fmt.Errorf("ColonyFacts differ with optimizations off and on: %#v", result)
	}
	if na.AsNumber(result["wasteRows"]) == 0 || na.AsNumber(result["upkeepItems"]) == 0 || na.AsNumber(result["resourceRows"]) == 0 {
		return fmt.Errorf("fixture read carries no waste, upkeep item or resource source rows to compare: %#v", result)
	}
	return nil
}
