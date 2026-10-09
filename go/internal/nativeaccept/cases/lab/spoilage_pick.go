package lab

import (
	"context"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "lab/spoilage-pick",
		Scope: "Spoilage preference (#2520): test/spoilage_pick spawns an old and a fresh stack of raw meat and of simple meals " +
			"near a hungry colonist, the fresh one closer and the old one outside vanilla's freshness bonus, and runs the real " +
			"bill-ingredient pick and meal pick. With the preference off both choose the closer fresh stack; with it on both " +
			"choose the old stack; with the old stack forbidden both still choose the fresh one. A Go snapshot test cannot see " +
			"the native Harmony patches reorder vanilla's candidates.",
		Start:       cases.Lab{Colonists: 1},
		RequiredOps: []string{"test/spoilage_pick"},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			reply, err := s.Harness().Call(ctx, "spoilage-pick", "test/spoilage_pick", nil)
			if err != nil {
				return err
			}
			if ok, _ := na.AsBool(reply["success"]); !ok {
				return fmt.Errorf("spoilage_pick: %#v", reply)
			}
			s.Report()["spoilage_pick"] = reply
			for _, kind := range []string{"bill", "meal"} {
				fresh, old := na.AsString(reply["fresh"+capital(kind)]), na.AsString(reply["old"+capital(kind)])
				for _, row := range []struct{ name, want string }{{"off", fresh}, {"on", old}, {"forbidden", fresh}} {
					picks, _ := na.AsMap(reply[row.name])
					if got := na.AsString(picks[kind]); got != row.want {
						return fmt.Errorf("spoilage_pick: %s pick with preference %s chose %q, want %q: %#v", kind, row.name, got, row.want, reply)
					}
				}
			}
			return nil
		},
	})
}

// capital maps the pick kind to the reply's stack-id suffix: bill picks meat, meal picks meals.
func capital(kind string) string {
	if kind == "bill" {
		return "Meat"
	}
	return "Meal"
}
