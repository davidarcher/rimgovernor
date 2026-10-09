package trade

import (
	"context"
	"fmt"
	"slices"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
)

func init() {
	cases.Register(cases.Case{
		Name:        "trade/settlement-roundtrip",
		Scope:       "Autonomous acquisition in one Core world: a home nutrition shortage and a nearby legal settlement cause normal controller packing, departure, world travel, live-priced purchase and home return. A Go snapshot cannot prove native packing, travel, settlement transfer or home entry. Only native departed crew, paid seller stock transferred to caravan cargo, and the same crew home with previously absent purchased meals establish success. No fixture creates a Project, caravan, trade or return order.",
		Start:       cases.Fixture{On: cases.Fixture{On: cases.Lab{Colonists: 6}, Op: "test/food_channels_prepare"}, Op: "test/trade_fixture", Args: map[string]any{"action": "roundtrip_setup"}},
		RequiredOps: []string{"test/trade_fixture"}, Quiet: na.QuietRequired,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Acquisition, routinefamily.Supply, routinefamily.Trade, routinefamily.Work}, NativeTimeout: 15 * time.Second, Prefix: "trade-roundtrip"},
		Budget: 8 * time.Minute, Crew: cases.Crew{Size: 6},
		// Native purchase/departure observations are required in order, so a suffix
		// resumed after those observations cannot claim the complete chain.
		NoCheckpoint: true, Run: runSettlementRoundtrip,
	})
}

func roundtripIDs(raw any) []string {
	var out []string
	for _, value := range na.AsSlice(raw) {
		out = append(out, na.AsString(value))
	}
	return out
}

func runSettlementRoundtrip(ctx context.Context, s cases.Session) error {
	prepared := s.Prepared()
	if ok, _ := na.AsBool(prepared["success"]); !ok || num(prepared["homeMeals"]) != 0 || num(prepared["sellerMeals"]) <= 0 || len(roundtripIDs(prepared["crew"])) != 6 {
		return fmt.Errorf("invalid roundtrip precondition: %#v", prepared)
	}
	seller := na.AsString(prepared["traderId"])
	sellerBefore := num(prepared["sellerMeals"])
	settlementTile := num(prepared["settlementTile"])
	homeCrew := roundtripIDs(prepared["crew"])
	var departedCrew []string
	var caravanID string
	var purchased, delivered bool
	var observeErr error
	read := func(h *na.Harness) (map[string]any, error) {
		return h.Call(ctx, "roundtrip-native-state", "test/trade_fixture", map[string]any{"action": "roundtrip_state", "traderId": seller})
	}
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 6 * time.Minute, Window: 2 * 60000, PollTicks: 250, Concern: policy.EnsureFoodSupply,
			Until: func(_ map[string]any) bool {
				state, e := read(s.Harness())
				if e != nil {
					observeErr = e
					return true
				}
				present := roundtripIDs(state["homeCrew"])
				for _, raw := range na.AsSlice(state["caravans"]) {
					c, _ := na.AsMap(raw)
					crew := roundtripIDs(c["crew"])
					if caravanID == "" && len(crew) > 0 && !slices.ContainsFunc(crew, func(id string) bool { return !slices.Contains(homeCrew, id) || slices.Contains(present, id) }) {
						caravanID = na.AsString(c["id"])
						departedCrew = crew
						s.Report()["native_departure"] = state
					}
					if na.AsString(c["id"]) == caravanID && caravanID != "" && num(c["tile"]) == settlementTile && num(c["meals"]) > 0 && num(state["sellerMeals"]) < sellerBefore && num(state["sellerSilver"]) > 10000 {
						purchased = true
						s.Report()["native_purchase"] = state
					}
				}
				if purchased && len(departedCrew) > 0 && !slices.ContainsFunc(departedCrew, func(id string) bool { return !slices.Contains(present, id) }) && num(state["homeMeals"]) > 0 {
					delivered = true
					s.Report()["native_delivery"] = state
				}
				return delivered
			}},
		Audit: func(_ context.Context, h *na.Harness, report na.Report) error {
			if observeErr != nil {
				return observeErr
			}
			state, e := read(h)
			if e != nil {
				return e
			}
			report["native_final"] = state
			present := roundtripIDs(state["homeCrew"])
			if !purchased || !delivered || len(departedCrew) == 0 || slices.ContainsFunc(departedCrew, func(id string) bool { return !slices.Contains(present, id) }) || num(state["homeMeals"]) <= 0 {
				return fmt.Errorf("roundtrip incomplete: departed=%v purchased=%v delivered=%v native=%#v", departedCrew, purchased, delivered, state)
			}
			return nil
		},
	})
	return err
}
