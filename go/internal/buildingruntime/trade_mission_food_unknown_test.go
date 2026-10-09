package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type missionPurchaseNative struct {
	RoundsTradeSource
	core    *roundsNative
	catalog *bridge.DefinitionCatalog
	session bridge.TradeSessionRead
	sheet   bridge.TradeSheetRead
}

func (n *missionPurchaseNative) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	return n.core.ReadColonyFacts(ctx, id, planning)
}
func (n *missionPurchaseNative) FrameTables(ctx context.Context, id *c.Identity) (bridge.Tables, error) {
	tables, err := n.core.FrameTables(ctx, id)
	tables.Catalog = n.catalog
	return tables, err
}
func (n *missionPurchaseNative) ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error) {
	return n.session, bridge.Result{}, nil
}
func (n *missionPurchaseNative) ReadTradeSheet(context.Context, *c.Identity) (bridge.TradeSheetRead, bridge.Result, error) {
	return n.sheet, bridge.Result{}, nil
}

func missionPurchaseCatalog(t *testing.T) *bridge.DefinitionCatalog {
	t.Helper()
	return recordedcatalog.Catalog(t)
}

func TestMissionUnknownFoodHoldsBuyingButKnownZeroReturns(t *testing.T) {
	catalog := missionPurchaseCatalog(t)
	for _, known := range []bool{false, true} {
		name := "unknown"
		if known {
			name = "known_zero"
		}
		t.Run(name, func(t *testing.T) {
			ctx := context.Background()
			r, db, _, _, core := roundsFixture(t)
			foodPlanFixture(core.reply.GetObserved())
			if !known {
				core.reply.GetObserved().FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
				core.reply.GetObserved().Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
			}
			// Force a measured plan's gap to zero through the existing fixture control.
			// The control does not turn an unknown food plan into a known one.
			r.foodGapZero = true
			state := r.player.session.State()
			m := tradeMissionFixture()
			m.HomeColony = state.Snapshot.Colony
			m.HomeMap = state.Snapshot.Map
			m.Phase = domain.TradeMissionPlanned
			m.Demand = []domain.CargoItem{{Definition: "MealSurvivalPack", Count: 10}}
			p, err := domain.NewTradeMissionProject("food-mission", 1, state.Snapshot, m)
			if err != nil {
				t.Fatal(err)
			}
			if err = db.CreateProject(ctx, p); err != nil {
				t.Fatal(err)
			}
			project, err := db.LoadProject(ctx, p.ID)
			if err != nil {
				t.Fatal(err)
			}
			m.Phase = domain.TradeMissionBuying
			record, err := m.Record()
			if err != nil {
				t.Fatal(err)
			}
			if _, err = db.RecordProject(ctx, p.ID, project.Revision, record); err != nil {
				t.Fatal(err)
			}
			project, err = db.LoadProject(ctx, p.ID)
			if err != nil {
				t.Fatal(err)
			}
			caravan := &bridge.CaravanJourney{ID: "caravan", MassUsage: domain.Known(5.0), MassCapacity: domain.Known(35.0)}
			target := domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: m.Settlement, Caravan: caravan.ID}
			native := &missionPurchaseNative{core: core, catalog: catalog, session: bridge.TradeSessionRead{Trader: target.Key(), Open: true}, sheet: bridge.TradeSheetRead{Trader: target.Key(), CanTradeNow: true, Rows: []bridge.TradeSheetRow{
				{LineID: "meal", DefName: "MealSurvivalPack", TraderCount: 20, BuyPrice: 2, BuyPriceKnown: true, TraderWillTrade: true, TraderWillTradeKnown: true},
				{LineID: "silver", DefName: "Silver", Currency: true, CurrencyKnown: true, ColonyCount: 100},
			}}}
			planner := &RoundsTradePlanner{reviewer: r, native: native}
			result, handled, err := planner.missionTrade(ctx, r.player.epoch, state, store.Rounds{}, project, m, caravan)
			if err != nil || !handled || result.Plan != "" {
				t.Fatal(result, handled, err)
			}
			if food, measured := r.census.foodPlan.Value(); measured != known || (measured && food.GapPerDay != 0) {
				t.Fatal("fixture did not distinguish unknown from measured zero", food, measured)
			}
			saved, err := db.LoadProject(ctx, p.ID)
			if err != nil {
				t.Fatal(err)
			}
			mission, err := domain.DecodeTradeMission(saved.Project.Record)
			if err != nil {
				t.Fatal(err)
			}
			want := domain.TradeMissionBuying
			if known {
				want = domain.TradeMissionReturning
			}
			if mission.Phase != want || mission.PurchaseCommitted || len(mission.ReturnGoods) != 0 || len(saved.History) != 0 || saved.Project.Status != domain.ProjectOpen {
				t.Fatal("wrong mission transition", mission, saved)
			}
			if !known && (saved.Revision != project.Revision || result.Verdict != fieldUnavailable("food_plan")) {
				t.Fatal("unknown demand mutated intent or lost refusal", saved, result)
			}
		})
	}
}
