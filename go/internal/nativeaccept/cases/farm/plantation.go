package farm

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// farm/plantation-rows (#2292): the private test/plantation_prepare fixture
// sows an oak (growth 0.8) and a rice plant (growth 0.9) in an oak growing
// zone. The colony read's acquisition census must list the sown oak as a
// plantation row carrying its growth fraction, the fixture must report it
// designatable, and the rice crop must never become a row.
func init() {
	cases.Register(cases.Case{
		Name: "farm/plantation-rows",
		Scope: "Native plantation census: a sown oak in a growing zone is an acquisition row marked plantation with its growth fraction " +
			"and is designatable by chop acquisition; a rice plant in the same zone is not a row (#2292).",
		Start:  cases.Fixture{Op: "test/plantation_prepare", On: cases.LabStart()},
		Budget: 3 * time.Minute,
		Run:    runPlantationRows,
	})
}

func runPlantationRows(ctx context.Context, s cases.Session) error {
	report, h, prepared := s.Report(), s.Harness(), s.Prepared()
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("plantation fixture refused: %v", prepared)
	}
	oak, _ := na.AsMap(prepared["oak"])
	rice, _ := na.AsMap(prepared["rice"])
	report["fixture"] = prepared
	if eligible, _ := na.AsBool(oak["eligible"]); !eligible {
		return fmt.Errorf("sown oak in the growing zone is not chop-eligible: %#v", oak)
	}
	if accepts, _ := na.AsBool(oak["designatorAccepts"]); !accepts {
		return fmt.Errorf("the harvest-wood designator refuses the sown oak: %#v", oak)
	}
	if eligible, _ := na.AsBool(rice["eligible"]); eligible {
		return fmt.Errorf("a rice plant in the growing zone became chop-eligible: %#v", rice)
	}
	reply, err := h.Wire(ctx, "plantation-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "planning": true})
	if err != nil {
		return err
	}
	_, facts, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	var oakRow map[string]any
	for _, raw := range na.AsSlice(facts["acquisition"]) {
		row, _ := na.AsMap(raw)
		source, _ := na.AsMap(row["source"])
		switch na.AsString(source["id"]) {
		case na.AsString(rice["id"]):
			return fmt.Errorf("a rice plant in the growing zone is an acquisition row: %#v", row)
		case na.AsString(oak["id"]):
			oakRow = row
		}
	}
	report["oak_row"] = oakRow
	if oakRow == nil {
		return fmt.Errorf("the sown oak is not an acquisition row: %#v", facts["acquisition"])
	}
	if plantation, _ := na.AsBool(oakRow["plantation"]); !plantation {
		return fmt.Errorf("the oak row is not marked plantation: %#v", oakRow)
	}
	if growth := na.AsNumber(oakRow["growth"]); growth < 0.79 || growth > 0.81 {
		return fmt.Errorf("the oak row growth %.3f is not the fixture's 0.8: %#v", growth, oakRow)
	}
	return nil
}
