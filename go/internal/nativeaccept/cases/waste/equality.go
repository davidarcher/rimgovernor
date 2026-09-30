// The waste/colony_facts_equality case is the permanent ColonyFacts
// equality check (#1296): on the waste fixture (item, corpse and waste rows
// whose order matters) the native snapshot read with every read
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
		Run:        runEquality,
	})
}

func runEquality(ctx context.Context, s cases.Session) error {
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
	if na.AsNumber(result["wasteRows"]) == 0 || na.AsNumber(result["upkeepItems"]) == 0 {
		return fmt.Errorf("fixture read carries no waste or upkeep item rows to compare: %#v", result)
	}
	return nil
}
