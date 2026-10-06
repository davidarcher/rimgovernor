// trade/browse is #2168's acceptance: the negotiator's session is also the
// look at a caravan's priced goods. With a medicine shortfall the caravan's
// session opens, the planner records the sheet's offers and either stages and
// accepts lines or cancels without trading; the journal must show the open
// and a terminal phase, and silver may leave the colony only through an
// accept. Open, read, cancel-without-trading is the path this case proves.
package trade

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	cases.Register(cases.Case{
		Name: "trade/browse",
		Scope: "Caravan offer records (#2168): the negotiator's session reads the caravan's priced goods, and the planner " +
			"either buys the staged lines or cancels without trading. A Go snapshot test cannot cover it: the open, " +
			"sheet read and cancel are native operations whose absence of side effects only a live session shows.",
		Start: cases.Fixture{
			On:   cases.LabStart(),
			Op:   "test/trade_fixture",
			Args: map[string]any{"action": "routine_setup", "silver": roundsSilver, "medicine": 30},
		},
		Serve:  &cases.ServeSpec{Families: []string{"trade"}, NativeTimeout: 15 * time.Second, Prefix: "trade-browse"},
		Budget: 15 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			prepared := s.Prepared()
			s.Report()["trade_prepared"] = prepared
			if success, _ := na.AsBool(prepared["success"]); !success {
				return fmt.Errorf("routine_setup: no trader caravan spawned: %#v", prepared)
			}
			deficit := false
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{
					Watch: roundsWindow, Concern: policy.TradeWithCaravan,
					Until: func(sample map[string]any) bool {
						if bound, _ := na.AsBool(sample["concern_bound"]); !bound {
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
					review, err := journal.LoadRounds(ctx)
					if err != nil {
						return err
					}
					binding, ok := review.Incident(policy.TradeWithCaravan)
					if !ok {
						return fmt.Errorf("TradeWithCaravan never bound an occurrence")
					}
					incident, err := journal.LoadIncident(ctx, binding.Incident)
					if err != nil {
						return err
					}
					phases := map[string]bool{}
					for _, m := range incident.Methods {
						for _, kind := range []string{"open", "accept", "end"} {
							phases[kind] = phases[kind] || strings.HasPrefix(string(m.Method), "trade-"+kind+"-")
						}
					}
					report["trade_phases"] = phases
					if !phases["open"] {
						return fmt.Errorf("no session was opened for the caravan: %v", phases)
					}
					if !phases["accept"] && !phases["end"] {
						return fmt.Errorf("the opened session never ended: %v", phases)
					}
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
					silver := 0.0
					for _, v := range na.AsSlice(facts["resources"]) {
						row, _ := na.AsMap(v)
						if na.AsString(row["defName"]) == "Silver" {
							silver += na.AsNumber(row["units"])
						}
					}
					report["trade_silver"] = map[string]any{"silver": silver, "silver_spawned": roundsSilver}
					if !phases["accept"] && silver != roundsSilver {
						return fmt.Errorf("a cancelled session moved silver: %v of %d", silver, roundsSilver)
					}
					return nil
				},
			})
			return err
		},
	})
}
