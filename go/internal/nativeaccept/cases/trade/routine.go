// trade/routine is issue #234's acceptance, restored natively for #1158 after\n// the #749 snapshot conversion left the session/accept path unchecked: a colony with no medicine and
// one silver stack, a trader caravan arriving from the map edge with herbal
// medicine, and the live service with the trade family alone. The
// TradeWithCaravan goal must stand while the caravan walks in, open a
// session (native walks the negotiator to the arrived trader), stage the
// observed medicine shortfall against the observed silver, and accept: the native
// resource census afterwards holds medicine the colony did not have and
// less silver than the fixture spawned.
package trade

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// routineWindow bounds the watch: the caravan's walk in, the negotiator's
// walk and the three session phases each land in the stop between windows.
const routineWindow = 10 * time.Minute

// routineSilver is the silver stack the fixture spawns (under the 500 stack
// limit); the medicine buy must come out of it.
const routineSilver = 400

func init() {
	cases.Register(cases.Case{
		Name: "trade/routine",
		Scope: "TradeWithCaravan (#234): an arriving caravan sells the observed medicine shortfall " +
			"from observed silver through the routine trade family alone -- walked open, staged lines, " +
			"accept -- proven by the native resource census after the service stops.",
		Start: cases.Fixture{
			On:   cases.LabStart(),
			Op:   "test/trade_fixture",
			Args: map[string]any{"action": "routine_setup", "silver": routineSilver, "medicine": 30},
		},
		// Every need frozen: the negotiator's walks are the only pawn time
		// the case is about.
		Serve:  &cases.ServeSpec{Families: []string{"trade"}, NativeTimeout: 15 * time.Second, Prefix: "trade-routine"},
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			prepared := s.Prepared()
			s.Report()["trade_prepared"] = prepared
			if success, _ := na.AsBool(prepared["success"]); !success {
				return fmt.Errorf("routine_setup: no trader caravan spawned: %#v", prepared)
			}
			if na.AsNumber(prepared["colonyMedicine"]) != 0 {
				return fmt.Errorf("routine_setup: colony medicine remains: %#v", prepared)
			}
			stocked, _ := prepared["stocked"].([]any)
			for _, row := range stocked {
				if m, ok := na.AsMap(row); !ok {
					return fmt.Errorf("routine_setup: unreadable stock row: %#v", row)
				} else if listed, _ := na.AsBool(m["listed"]); !listed {
					return fmt.Errorf("routine_setup: the caravan does not list the stocked medicine: %#v", row)
				}
			}
			deficit := false
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: routineWindow, Goal: policy.TradeWithCaravan,
					// The goal recovering after a deficit is the trade
					// settling (an accept, or the need gone); the census
					// audit below says which.
					Until: func(sample map[string]any) bool {
						bound, _ := na.AsBool(sample["goal_bound"])
						if !bound {
							return false
						}
						if na.AsString(sample["need"]) == "unmet" {
							deficit = true
							return false
						}
						return deficit && na.AsString(sample["need"]) == "met"
					},
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := na.OpenStoreWithRetry(ctx, s.Config().Output+"/service.sqlite")
					if err != nil {
						return err
					}
					defer journal.Close()
					goal, err := sustainedfood.SampleGoal(ctx, journal, policy.TradeWithCaravan)
					if err != nil {
						return err
					}
					report["trade_final"] = goal
					identity, err := na.ReadIdentity(ctx, h, "audit-identity")
					if err != nil {
						return err
					}
					reply, err := h.Wire(ctx, "audit-colony-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
					if err != nil {
						return err
					}
					_, facts, err := na.Outcome(reply, "observed")
					if err != nil {
						return fmt.Errorf("colony facts: %w", err)
					}
					// An item the census holds none of has no row at all.
					resources := map[string]float64{}
					for _, v := range na.AsSlice(facts["resources"]) {
						row, _ := na.AsMap(v)
						resources[na.AsString(row["defName"])] += na.AsNumber(row["units"])
					}
					count := func(name string) float64 { return resources[name] }
					medicine := 0.0
					// The Core medicines the lab's trader carries.
					for _, name := range []string{"MedicineUltratech", "MedicineIndustrial", "MedicineHerbal"} {
						medicine += count(name)
					}
					silver := count("Silver")
					report["trade_census"] = map[string]any{"medicine": medicine, "silver": silver, "silver_spawned": routineSilver}
					if !deficit {
						return fmt.Errorf("TradeWithCaravan never measured a deficit with the caravan present")
					}
					if medicine <= 0 {
						return fmt.Errorf("no medicine was bought: census medicine %v, silver %v of %d", medicine, silver, routineSilver)
					}
					if silver >= routineSilver {
						return fmt.Errorf("medicine appeared without silver leaving: census medicine %v, silver %v of %d", medicine, silver, routineSilver)
					}
					return nil
				},
			})
			return err
		},
	})
}
