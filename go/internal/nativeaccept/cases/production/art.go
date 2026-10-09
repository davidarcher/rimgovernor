package production

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// production/art (#1195, epic #1172) runs the art family end to end on the
// lab. test/art builds the artist's furnished bedroom A below its build
// tier's impressiveness target (policy.ImpressivenessLevels.Baseline) with beauty its
// weakest stat, and an ascetic neighbour's plain room B. Jade, a sculpting
// table, a little silver and no medicine (a purchase need, so art for sale
// is wanted) complete it.
//
// Phase 1 (sleeping, art, trade): MaintainArt pins a sculpture bill to the
// artist, the sleeping upkeep installs the packed piece in room A until it
// reaches its target, and art for sale leaves surplus packed art. Phase 2: an
// exotic caravan arrives and TradeWithCaravan sells the surplus sculpture
// through SelectTrade's art step, matched by packed item id (#1194).
//
// Why not a snapshot test: the sculpture is vanilla production (a packed
// MinifiedThing with a quality roll), the install is a native relocate
// of that packed item, room impressiveness is vanilla room stats, and the
// sale matches Tradeable.FirstThingColony on a live trade sheet to the
// packed item id ReadPackedItems reported -- a read contract only a live
// game proves.
const artOp = "test/art"

func init() {
	cases.Register(cases.Case{
		Name:        "production/art",
		Scope:       "An artist sculpts and the piece is installed until their bedroom reaches its tier target; with an exotic caravan present the surplus packed sculpture sells (#1195). Native: vanilla sculpture production and room stats, the packed install, and the trade row's FirstThingColony matching the packed item id.",
		Start:       cases.Fixture{Op: artOp, Args: map[string]any{"action": "prepare"}, On: cases.Lab{Colonists: 2}},
		RequiredOps: []string{artOp},
		Quiet:       na.QuietRequired, QuietWorld: true,
		Serve:  &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Sleeping, routinefamily.Art, routinefamily.Trade}, NativeTimeout: 15 * time.Second, Prefix: "art"},
		Budget: 45 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "two phases on one colony: a sculpture, its install and a sale piece take two in-game days before the caravan phase can sell the surplus",
		Run: runArt,
	})
}

func runArt(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("fixture: %v", prepared)
	}
	a, _, err := artRooms(prepared)
	if err != nil {
		return err
	}
	catalog, err := cases.Catalog(ctx, s.Harness().Client, s.Identity())
	if err != nil {
		return err
	}
	levels, err := catalog.ImpressivenessLevels()
	if err != nil {
		return err
	}
	target := artTarget(prepared, levels)
	report["target"] = target
	if target <= 0 || na.AsNumber(a["impressiveness"]) >= target {
		return fmt.Errorf("room A is not below its target %v: %v", target, prepared["rooms"])
	}
	// Phase 1: sculpt, install, and sculpt for sale. Three in-game days: a
	// large sculpture (30000 work), its install and a sale piece.
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 25 * time.Minute, Window: 180000, Concern: policy.MaintainArt, Extra: []policy.ConcernID{policy.TradeWithCaravan}},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := h.Call(ctx, "audit-install", artOp, map[string]any{"action": "audit"})
			if err != nil {
				return err
			}
			report["installed"] = after
			return artInstallVerdict(after, target)
		},
	})
	if err != nil {
		return err
	}
	// Phase 2: the caravan buys the surplus.
	var surplus []string
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 10 * time.Minute, Window: 30000, Concern: policy.TradeWithCaravan, Extra: []policy.ConcernID{policy.MaintainArt}},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			before, err := h.Call(ctx, "audit-before-trade", artOp, map[string]any{"action": "audit"})
			if err != nil {
				return err
			}
			report["before_trade"] = before
			surplus = artPackedIDs(before)
			trader, err := h.Call(ctx, "trader", artOp, map[string]any{"action": "trader"})
			if err != nil {
				return err
			}
			report["trader"] = trader
			if ok, _ := na.AsBool(trader["success"]); !ok {
				return fmt.Errorf("trader: %v", trader)
			}
			if len(na.AsSlice(trader["buyable"])) == 0 {
				return fmt.Errorf("the caravan buys none of the packed sculptures %v: %v", surplus, trader)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := h.Call(ctx, "audit-after-trade", artOp, map[string]any{"action": "audit"})
			if err != nil {
				return err
			}
			report["after_trade"] = after
			return artSaleVerdict(report["before_trade"], after, surplus)
		},
	})
	return err
}

// artRooms splits a fixture read's rooms into the artist's (A) and the
// neighbour's (B).
func artRooms(read map[string]any) (a, b map[string]any, err error) {
	for _, row := range na.AsSlice(read["rooms"]) {
		room, _ := na.AsMap(row)
		if artist, _ := na.AsBool(room["artist"]); artist {
			a = room
		} else {
			b = room
		}
	}
	if a == nil || b == nil {
		return nil, nil, fmt.Errorf("fixture rooms unreadable: %v", read["rooms"])
	}
	return a, b, nil
}

// artTarget is room A's impressiveness target: the tier baseline from the
// fixture's faction tech level and finished research.
func artTarget(prepared map[string]any, levels policy.ImpressivenessLevels) float64 {
	var finished []policy.ResearchProjectID
	for _, r := range na.AsSlice(prepared["research"]) {
		finished = append(finished, policy.ResearchProjectID(na.AsString(r)))
	}
	tier, _ := policy.SelectTechTier(domain.Known(finished), domain.Known(na.AsString(prepared["techLevel"]))).Value()
	return levels.Baseline(tier)
}

// artPackedIDs are the packed sculptures a fixture audit lists.
func artPackedIDs(audit map[string]any) []string {
	var out []string
	for _, row := range na.AsSlice(audit["packed"]) {
		m, _ := na.AsMap(row)
		out = append(out, na.AsString(m["id"]))
	}
	return out
}

// artInstallVerdict: a sculpture stands in room A, room A reached its
// target, and surplus packed art is left to sell.
func artInstallVerdict(audit map[string]any, target float64) error {
	a, _, err := artRooms(audit)
	if err != nil {
		return err
	}
	var failures []string
	if len(na.AsSlice(a["sculptures"])) == 0 {
		failures = append(failures, "no sculpture installed in the artist's room")
	}
	if na.AsNumber(a["impressiveness"]) < target {
		failures = append(failures, fmt.Sprintf("room A impressiveness %v below its target %v", a["impressiveness"], target))
	}
	if len(artPackedIDs(audit)) == 0 {
		failures = append(failures, "no surplus packed sculpture to sell")
	}
	if len(failures) > 0 {
		return fmt.Errorf("%v (audit %v)", failures, audit)
	}
	return nil
}

// artSaleVerdict: a surplus sculpture left the colony for the caravan and
// the colony's silver rose.
func artSaleVerdict(before any, after map[string]any, surplus []string) error {
	prior, _ := na.AsMap(before)
	left := map[string]bool{}
	for _, id := range artPackedIDs(after) {
		left[id] = true
	}
	sold := 0
	for _, id := range surplus {
		if !left[id] {
			sold++
		}
	}
	var failures []string
	if sold == 0 {
		failures = append(failures, fmt.Sprintf("none of the surplus sculptures %v sold", surplus))
	}
	// The caravan may have left by the audit; its stock is checked only
	// while it stays.
	if na.AsNumber(after["traders"]) > 0 && len(na.AsSlice(after["traderArt"])) == 0 {
		failures = append(failures, "the caravan holds no sculpture")
	}
	if na.AsNumber(after["colonySilver"]) <= na.AsNumber(prior["colonySilver"]) {
		failures = append(failures, fmt.Sprintf("colony silver did not rise: %v -> %v", prior["colonySilver"], after["colonySilver"]))
	}
	if len(failures) > 0 {
		return fmt.Errorf("%v (audit %v)", failures, after)
	}
	return nil
}
