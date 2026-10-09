package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"slices"
	"strings"
)

type TradeMissionNative interface {
	ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	ReadPawnCargo(context.Context, *c.Identity, []string) (map[string]int64, bridge.Result, error)
}

// TradeMissionDelivered requires actual cargo held by the returned native crew.
// An accept receipt, caravan disappearance, or existing home stock proves none.
func TradeMissionDelivered(m domain.TradeMission, world *bridge.WorldProgressionRead, cargo map[string]int64) bool {
	if m.Validate() != nil || !m.PurchaseCommitted || len(m.ReturnGoods) == 0 || !tradeMissionCrewAtHome(m, world) {
		return false
	}
	for _, item := range m.ReturnGoods {
		packed := uint64(0)
		for _, seed := range m.Pack {
			if seed.Definition == item.Definition {
				packed = seed.Count
			}
		}
		if cargo[item.Definition] < int64(item.Count+packed) {
			return false
		}
	}
	return true
}

// TradeMissionReturnSafe refreshes asymmetric home routes, current food/rot and
// capacity after trading. Unknown facts hold an order, never admit a trip.
func TradeMissionReturnSafe(m domain.TradeMission, caravan *bridge.CaravanJourney) bool {
	if caravan == nil {
		return false
	}
	in := policy.MissionReturnFacts{Mass: caravan.MassUsage, Capacity: caravan.MassCapacity, FoodDays: missionOptional(caravan.FoodDays, caravan.FoodDaysKnown), RotDays: caravan.FoodRotDays}
	for _, route := range caravan.HomeRoutes {
		if route.DestinationTile == m.HomeTile && route.Reachable && route.EstimatedTicksKnown && route.EstimatedTicks >= 0 {
			in.RouteTicks = domain.Known(route.EstimatedTicks)
			break
		}
	}
	return policy.TradeMissionReturnSafe(in)
}
func missionCaravan(m domain.TradeMission, world *bridge.WorldProgressionRead) *bridge.CaravanJourney {
	var found *bridge.CaravanJourney
	for i := range world.Caravans {
		row := &world.Caravans[i]
		overlap := slices.ContainsFunc(m.Crew, func(id domain.PawnID) bool { return slices.Contains(row.PawnIDs, string(id)) })
		if !overlap {
			continue
		}
		if found != nil || len(row.PawnIDs) != len(m.Crew) || slices.ContainsFunc(m.Crew, func(id domain.PawnID) bool { return !slices.Contains(row.PawnIDs, string(id)) }) {
			return nil
		}
		found = row
	}
	return found
}

func (r *RoundsTradePlanner) mission(call, epoch context.Context, state ControlState, review store.Rounds, arbiter *stepArbiter) (RoundsTradeResult, bool, error) {
	if !state.Enabled {
		return RoundsTradeResult{Verdict: BuildingReasonDisabled}, true, nil
	}
	source, ok := r.native.(TradeMissionNative)
	if !ok {
		return RoundsTradeResult{}, false, nil
	}
	projects, err := r.reviewer.player.journal.OpenProjects(call, domain.TradeMissionConcern)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	if len(projects) == 0 {
		return r.createMission(call, state, review)
	}
	project := projects[0]
	m, err := domain.DecodeTradeMission(project.Project.Record)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	world, _, err := source.ReadWorldProgression(call, boundary.Identity(state.Snapshot), false)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	if !tradeMissionCurrentRead(state.Snapshot, &world) {
		return RoundsTradeResult{}, true, ErrControl
	}
	project, err = r.reviewer.player.journal.ReviewProject(call, project.Project.ID, project.Revision, state.Snapshot, domain.Tick(world.Context.GetTick()), domain.FindingUnmet)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	// Shared Hands owns dispatch and uncertain work. Never create a second
	// formation or purchase while its method remains live.
	for _, method := range project.Methods {
		plan, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		if store.PlanOpen(plan) {
			return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
		}
	}
	caravan := missionCaravan(m, &world)
	if m.Phase == domain.TradeMissionPlanned {
		id, err := r.reviewer.AdmitTradeMissionDeparture(call, epoch, state, project, &world, arbiter)
		return RoundsTradeResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: tradeWalkTicks}, true, err
	}
	if m.Phase == domain.TradeMissionDeparting || m.Phase == domain.TradeMissionOutbound {
		phase, _, known := TradeMissionDepartureEvidence(m, &world)
		if known && phase != m.Phase {
			m.Phase = phase
			return r.missionRecord(call, project, m)
		}
	}
	if caravan == nil {
		if tradeMissionCrewAtHome(m, &world) {
			crew := make([]string, len(m.Crew))
			for i, id := range m.Crew {
				crew[i] = string(id)
			}
			cargo, _, err := source.ReadPawnCargo(call, boundary.Identity(state.Snapshot), crew)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			if TradeMissionDelivered(m, &world, cargo) {
				m.Phase = domain.TradeMissionDelivered
			} else if !m.PurchaseCommitted {
				m.Phase = domain.TradeMissionEnded
			} else {
				// Vanilla unloads individually after home entry. If a read missed
				// held cargo, delivery remains unknown; loose stock is no substitute.
				return RoundsTradeResult{Verdict: BuildingReasonExistingWork}, true, nil
			}
			out, _, err := r.missionRecord(call, project, m)
			if err != nil {
				return out, true, err
			}
			saved, err := r.reviewer.player.journal.LoadProject(call, project.Project.ID)
			if err == nil {
				_, err = r.reviewer.player.journal.ReviewProject(call, saved.Project.ID, saved.Revision, state.Snapshot, domain.Tick(world.Context.GetTick()), domain.FindingMet)
			}
			return out, true, err
		}
		// Crew explicitly dead or absent everywhere ends without delivery.
		visible := false
		for _, row := range world.Maps {
			for _, id := range m.Crew {
				visible = visible || slices.Contains(row.PawnIDs, string(id))
			}
		}
		for _, row := range world.Assemblies {
			for _, id := range m.Crew {
				visible = visible || slices.Contains(row.PawnIDs, string(id))
			}
		}
		for _, row := range world.Caravans {
			for _, id := range m.Crew {
				visible = visible || slices.Contains(row.PawnIDs, string(id))
			}
		}
		if !visible {
			m.Phase = domain.TradeMissionEnded
			out, _, err := r.missionRecord(call, project, m)
			if err == nil {
				saved, e := r.reviewer.player.journal.LoadProject(call, project.Project.ID)
				err = e
				if err == nil {
					_, err = r.reviewer.player.journal.ReviewProject(call, saved.Project.ID, saved.Revision, state.Snapshot, domain.Tick(world.Context.GetTick()), domain.FindingMet)
				}
			}
			return out, true, err
		}
		return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
	}
	if m.Phase == domain.TradeMissionBuying {
		return r.missionTrade(call, epoch, state, review, project, m, caravan)
	}
	if m.Phase == domain.TradeMissionReturning {
		session, _, err := r.native.ReadTradeSession(call, boundary.Identity(state.Snapshot))
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		target := domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: m.Settlement, Caravan: caravan.ID}
		if session.Trader == target.Key() {
			if missionAttempted(project, "trade-end") {
				return RoundsTradeResult{Verdict: BuildingReasonExistingWork}, true, nil
			}
			value, err := domain.NewTradeEnd(target.Key(), m.Negotiator, domain.TradeEndCancel, false)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			value, err = value.WithParticipant(target)
			if err != nil {
				return RoundsTradeResult{}, true, err
			}
			return r.missionTradeCommit(call, epoch, state, project, m, value)
		}
		if caravan.Moving && caravan.Destination != nil && *caravan.Destination == m.HomeTile {
			// Enter uses UnloadIndividually: inventory survives entry, then
			// pawn jobs unload it. Observe each final travel tick before those
			// jobs can erase the existing holder evidence.
			ticks := uint32(tradeWalkTicks)
			for _, route := range caravan.HomeRoutes {
				if route.DestinationTile == m.HomeTile && route.EstimatedTicksKnown && route.EstimatedTicks <= int64(tradeWalkTicks) {
					ticks = 1
				}
			}
			return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: ticks}, true, nil
		}
		if !TradeMissionReturnSafe(m, caravan) {
			return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
		}
		departure, err := domain.NewCaravanDeparture(m.Crew, nil, m.HomeTile)
		if missionAttempted(project, "trade-return") {
			return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
		}
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewCaravanDepartureAction(domain.ActionID(string(id)+"-home"), departure)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		return r.missionCommit(call, epoch, state, project, m, id, action, "trade-return")
	}
	return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
}

func (r *RoundsTradePlanner) missionRecord(ctx context.Context, p store.ProjectState, m domain.TradeMission) (RoundsTradeResult, bool, error) {
	record, err := m.Record()
	if err == nil {
		_, err = r.reviewer.player.journal.RecordProject(ctx, p.Project.ID, p.Revision, record)
	}
	if err == nil && (m.Phase == domain.TradeMissionDelivered || m.Phase == domain.TradeMissionEnded) {
		memory := &r.reviewer.tradeAcquisition
		memory.mu.Lock()
		for owner, id := range memory.active {
			if strings.HasSuffix(id, ":settlement:"+m.Settlement) {
				delete(memory.active, owner)
			}
		}
		memory.mu.Unlock()
	}
	return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, err
}

func missionAttempted(p store.ProjectState, method string) bool {
	return slices.ContainsFunc(p.History, func(row store.ProjectMethod) bool { return strings.HasPrefix(string(row.Method), method+"-") })
}
func (r *RoundsTradePlanner) missionCommit(call, epoch context.Context, state ControlState, p store.ProjectState, m domain.TradeMission, id domain.PlanID, a domain.Action, method string) (RoundsTradeResult, bool, error) {
	plan, err := domain.NewPlan(id, 1, []domain.Action{a})
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	record, err := m.Record()
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	if err = r.reviewer.player.current(call, epoch); err != nil {
		return RoundsTradeResult{}, true, err
	}
	if r.reviewer.player.session.State() != state {
		return RoundsTradeResult{}, true, ErrControl
	}
	_, err = r.reviewer.player.journal.CommitProjectMethodRecord(call, p.Project.ID, p.Revision, domain.MethodID(fmt.Sprintf("%s-%d", method, p.Admitted)), "", plan, record)
	return RoundsTradeResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: tradeWalkTicks}, true, err
}
func (r *RoundsTradePlanner) missionTradeCommit(call, epoch context.Context, state ControlState, p store.ProjectState, m domain.TradeMission, value domain.Trade) (RoundsTradeResult, bool, error) {
	id := domain.MintPlanID()
	a, err := domain.NewTradeAction(domain.ActionID(string(id)+"-trade"), value)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	return r.missionCommit(call, epoch, state, p, m, id, a, "trade-"+string(value.Kind()))
}

func (r *RoundsTradePlanner) missionTrade(call, epoch context.Context, state ControlState, review store.Rounds, p store.ProjectState, m domain.TradeMission, caravan *bridge.CaravanJourney) (RoundsTradeResult, bool, error) {
	target := domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: m.Settlement, Caravan: caravan.ID}
	trader := target.Key()
	session, _, err := r.native.ReadTradeSession(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	if m.PurchaseCommitted {
		m.Phase = domain.TradeMissionReturning
		return r.missionRecord(call, p, m)
	}
	if session.Trader != "" && session.Trader != trader {
		return RoundsTradeResult{}, false, nil
	}
	if session.Trader != trader {
		attempted := missionAttempted(p, "trade-open")
		if attempted {
			m.Phase = domain.TradeMissionReturning
			return r.missionRecord(call, p, m)
		}
		census, _, err := r.native.ListTraders(call, boundary.Identity(state.Snapshot))
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		if !slices.ContainsFunc(census.Traders, func(row bridge.TraderRead) bool { return row.ID == trader && row.CanTrade }) {
			m.Phase = domain.TradeMissionReturning
			return r.missionRecord(call, p, m)
		}
		value, err := domain.NewTradeOpen(trader, m.Negotiator, false)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		value, err = value.WithParticipant(target)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		return r.missionTradeCommit(call, epoch, state, p, m, value)
	}
	if !session.Open {
		return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, nil
	}
	sheet, _, err := r.native.ReadTradeSheet(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	lines, goods, err := r.missionPurchases(call, state, review, m, sheet, caravan)
	if errors.Is(err, policy.ErrTradeMissionFoodDemandUnknown) {
		return RoundsTradeResult{Verdict: fieldUnavailable("food_plan"), NativeWorkTicks: tradeWalkTicks}, true, nil
	}
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	if sheet.Trader != trader || !sheet.CanTradeNow || len(lines) == 0 {
		m.Phase = domain.TradeMissionReturning
		return r.missionRecord(call, p, m)
	}
	if len(tradeStagedLines(sheet)) == 0 {
		if missionAttempted(p, "trade-set_lines") {
			m.Phase = domain.TradeMissionReturning
			return r.missionRecord(call, p, m)
		}
		value, err := domain.NewTradeSetLines(trader, m.Negotiator, lines, false)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		value, err = value.WithParticipant(target)
		if err != nil {
			return RoundsTradeResult{}, true, err
		}
		return r.missionTradeCommit(call, epoch, state, p, m, value)
	}
	silver, _, known := tradeSheetSilver(sheet.Rows)
	if !sameTradeLines(lines, tradeStagedLines(sheet)) || !sheet.ColonyCanAfford || !policy.TradeMissionPaymentSafe(m.SilverBudget, missionOptional(silver, known), missionOptional(sheet.Balance, sheet.BalanceKnown)) || sheet.DealSignature == "" {
		m.Phase = domain.TradeMissionReturning
		return r.missionRecord(call, p, m)
	}
	m.ReturnGoods = goods
	m.PurchaseCommitted = true
	value, err := domain.NewTradeAccept(trader, m.Negotiator, sheet.DealSignature, false, false)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	value, err = value.WithParticipant(target)
	if err != nil {
		return RoundsTradeResult{}, true, err
	}
	return r.missionTradeCommit(call, epoch, state, p, m, value)
}

// missionPurchases feeds live priced candidates through the existing ranker.
// Demand comes from the current owning food/resource plans, bounded by saved
// authorization. It does not publish away goods into home supply candidates.
func (r *RoundsTradePlanner) missionPurchases(ctx context.Context, state ControlState, review store.Rounds, m domain.TradeMission, sheet bridge.TradeSheetRead, caravan *bridge.CaravanJourney) ([]domain.TradeLine, []domain.CargoItem, error) {
	reply, _, err := r.native.ReadColonyFacts(ctx, boundary.Identity(state.Snapshot), false)
	if err != nil {
		return nil, nil, err
	}
	tables, err := r.native.FrameTables(ctx, boundary.Identity(state.Snapshot))
	if err != nil {
		return nil, nil, err
	}
	if tables.Catalog == nil {
		return nil, nil, ErrControl
	}
	projection, err := observation.DecodeColony(reply, observation.Identity{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map, Tick: domain.Tick(reply.GetObserved().GetContext().GetTick())}, tables)
	if err != nil {
		return nil, nil, err
	}
	r.reviewer.planFood(&projection)
	targets := r.reviewer.resourceTargets(state.Snapshot)
	nutrition := map[string]domain.Fact[float64]{}
	for _, item := range m.Demand {
		nutrition[item.Definition] = missionCatalogStat(tables.Catalog, item.Definition, "Nutrition")
	}
	var rows []policy.MissionPurchaseRow
	for _, row := range sheet.Rows {
		rows = append(rows, policy.MissionPurchaseRow{LineID: row.LineID, DefName: row.DefName, TraderCount: row.TraderCount, Pawn: row.Pawn, Currency: row.Currency, BuyPrice: missionOptional(row.BuyPrice, row.BuyPriceKnown), WillTrade: missionOptional(row.TraderWillTrade, row.TraderWillTradeKnown), UnitMass: missionCatalogStat(tables.Catalog, row.DefName, "Mass")})
	}
	silver, _, known := tradeSheetSilver(sheet.Rows)
	return policy.PlanTradeMissionPurchases(policy.MissionPurchaseRequest{Mission: m, Targets: targets, Stock: projection.Facts.Resources, Food: projection.Facts.FoodPlan, FoodTargetDays: r.reviewer.policy.FoodTargetDays, Nutrition: nutrition, Rows: rows, Silver: missionOptional(silver, known), Mass: caravan.MassUsage, Capacity: caravan.MassCapacity})
}

func missionOptional[T any](v T, known bool) domain.Fact[T] {
	if known {
		return domain.Known(v)
	}
	return domain.Unknown[T]()
}
func missionCatalogStat(catalog *bridge.DefinitionCatalog, def, stat string) domain.Fact[float64] {
	value, err := catalog.StatValue(def, "", stat)
	return missionOptional(float64(value), err == nil)
}

func (r *RoundsTradePlanner) createMission(ctx context.Context, state ControlState, review store.Rounds) (RoundsTradeResult, bool, error) {
	for _, owner := range []string{"food", "resources"} {
		proposal := r.reviewer.TradeAcquisitionPlan(owner).Proposal
		if proposal == nil || proposal.Option.Kind != policy.TradeAcquireSettlement {
			continue
		}
		r.reviewer.tradeAcquisition.mu.Lock()
		m, ok := r.reviewer.tradeAcquisition.missions[proposal.Option.ID]
		r.reviewer.tradeAcquisition.mu.Unlock()
		if !ok || !r.reviewer.ClaimTradeAcquisition(owner, proposal.Option.ID) {
			continue
		}
		id := domain.ProjectID("project-trade-" + string(domain.MintPlanID()))
		p, err := domain.NewTradeMissionProject(id, 1, state.Snapshot, m)
		if err == nil {
			err = r.reviewer.player.journal.CreateProject(ctx, p)
		}
		return RoundsTradeResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: tradeWalkTicks}, true, err
	}
	return RoundsTradeResult{}, false, nil
}
