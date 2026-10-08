package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"slices"
)

type TradeMissionPackSource interface {
	ReadTradeAcquisition(context.Context, *c.Identity, *op.FormCaravanIntent) (*o.TradeAcquisition, bridge.Result, error)
}

// PrepareTradeMission is a read-only exact-pack calculation. The caller feeds
// its returned option to the existing acquisition planner. No planner registers
// automatic departure until the complete purchase/return driver is installed.
func PrepareTradeMission(ctx context.Context, source TradeMissionPackSource, world domain.GenerationSnapshot, homeTile int32, settlement bridge.SettlementFact, facts policy.RoundsFacts, thresholds policy.RoundsPolicy, silverBudget int64, demand []domain.CargoItem, claimed []domain.PawnID) (domain.TradeMission, policy.TradeAcquisitionOption, error) {
	fail := func(reason string) (domain.TradeMission, policy.TradeAcquisitionOption, error) {
		return domain.TradeMission{}, policy.TradeAcquisitionOption{}, errors.New(reason)
	}
	if world.Validate() != nil || source == nil || homeTile < 0 || settlement.Tile == homeTile || settlement.Player || settlement.TraderKind == "" {
		return fail("invalid_trade_destination")
	}
	canTrade, known := settlement.CanTrade.Value()
	if !known || !canTrade {
		return fail("settlement_unavailable")
	}
	silver, sk := facts.Silver().Value()
	reserve, rk := policy.TradeSilverReserve(facts.Colonists)
	if !sk || !rk || silverBudget <= 0 || silverBudget > math.MaxInt32 || silver < reserve || silverBudget > silver-reserve {
		return fail("insufficient_silver")
	}
	crew, reason := policy.TradeMissionCrew(facts, claimed)
	if reason != "" {
		return fail(string(reason))
	}
	capacity, known := policy.TradeMissionCarryCapacity(facts, crew)
	if !known {
		return fail("mass_unknown")
	}
	// Native packing requires a day of food before it returns route estimates.
	// Seed with that legal minimum; native measures silver mass rather than Go
	// inventing a unit weight for modded definitions.
	seedFood, reason := policy.TradeMissionFood(facts, crew, 1, capacity, thresholds)
	if reason != "" {
		return fail(string(reason))
	}
	seedCargo := append(seedFood, domain.CargoItem{Definition: "Silver", Count: uint64(silverBudget)})
	departure, err := domain.NewCaravanDeparture(crew, seedCargo, settlement.Tile)
	if err != nil {
		return fail("invalid_trade_pack")
	}
	preview := func(d domain.CaravanDeparture) (*o.TradePackEstimate, error) {
		pack := &op.FormCaravanIntent{DestinationTile: proto.Int32(d.DestinationTile())}
		for _, id := range d.Crew() {
			pack.PawnIds = append(pack.PawnIds, string(id))
		}
		for _, item := range d.Cargo() {
			pack.Cargo = append(pack.Cargo, &op.DefCount{DefName: proto.String(item.Definition), Count: proto.Int32(int32(item.Count))})
		}
		read, _, err := source.ReadTradeAcquisition(ctx, boundary.Identity(world), pack)
		if err != nil {
			return nil, err
		}
		if read == nil || read.Pack == nil {
			return nil, errors.New("pack_unknown")
		}
		return read.Pack, nil
	}
	seed, err := preview(departure)
	if err != nil {
		return fail(err.Error())
	}
	days, known := policy.TradeMissionRouteDays(seed, homeTile, settlement.Tile)
	if !known || seed.MassUsage == nil || seed.MassCapacity == nil || math.IsNaN(seed.GetMassUsage()) || math.IsNaN(seed.GetMassCapacity()) || math.IsInf(seed.GetMassUsage(), 0) || math.IsInf(seed.GetMassCapacity(), 0) || seed.GetMassUsage() < 0 {
		return fail("unknown_trip_facts")
	}
	// Calculate the whole food pack under the original native capacity minus
	// silver. Seed nutrition is not removed from home reserves twice.
	seedFoodMass := 0.0
	supply, _ := facts.AnimalUpkeep.Food.Value()
	for _, item := range seedFood {
		mass := math.MaxFloat64
		for _, stock := range supply.Stocks {
			if string(stock.DefName) == item.Definition {
				if unit, ok := stock.UnitMass.Value(); ok {
					mass = min(mass, unit)
				}
			}
		}
		if mass == math.MaxFloat64 {
			return fail("mass_unknown")
		}
		seedFoodMass += mass * float64(item.Count)
	}
	food, reason := policy.TradeMissionFood(facts, crew, days, seed.GetMassCapacity()-seed.GetMassUsage()+seedFoodMass, thresholds)
	if reason != "" {
		return fail(string(reason))
	}
	cargo := append(food, domain.CargoItem{Definition: "Silver", Count: uint64(silverBudget)})
	departure, err = domain.NewCaravanDeparture(crew, cargo, settlement.Tile)
	if err != nil {
		return fail("invalid_trade_pack")
	}
	final, err := preview(departure)
	if err != nil {
		return fail(err.Error())
	}
	if !policy.SafeTradeMissionPack(final, homeTile, settlement.Tile) {
		return fail("unsafe_trade_pack")
	}
	m := domain.TradeMission{HomeColony: world.Colony, HomeMap: world.Map, HomeTile: homeTile, Settlement: settlement.ID, SettlementTile: settlement.Tile, Crew: departure.Crew(), Negotiator: crew[0], SilverBudget: silverBudget, Demand: slices.Clone(demand), Pack: departure.Cargo(), Phase: domain.TradeMissionPlanned, ReturnHome: true}
	// Demand is canonicalized through the same definition/count contract.
	d, err := domain.NewCaravanDeparture(crew, demand, settlement.Tile)
	if err != nil {
		return fail("invalid_trade_demand")
	}
	m.Demand = d.Cargo()
	if err := m.Validate(); err != nil {
		return fail(err.Error())
	}
	days, _ = policy.TradeMissionRouteDays(final, homeTile, settlement.Tile)
	option := policy.TradeAcquisitionOption{ID: "settlement:" + settlement.ID, Kind: policy.TradeAcquireSettlement, Participant: domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: settlement.ID}, TraderKind: settlement.TraderKind, Negotiator: string(m.Negotiator), Eligible: domain.Known(true), SafeCrew: domain.Known(true), Packable: domain.Known(true), RoutesReachable: domain.Known(true), LeadDays: domain.Known(days), OutboundDays: domain.Known(float64(final.Outbound.GetEstimatedTicks()) / float64(domain.TicksPerDay)), ReturnDays: domain.Known(float64(final.Home.GetEstimatedTicks()) / float64(domain.TicksPerDay)), FoodDays: domain.Known(final.GetFoodDays()), RotDays: domain.Known(final.GetFoodRotDays()), LaborTicks: domain.Known(days * float64(domain.TicksPerDay) * float64(len(crew)))}
	return m, option, nil
}

// TradeMissionDepartureEvidence never authorizes a fresh formation after the
// planned phase. Lost receipts are resolved from exact native crew, and partial
// or conflicting crew evidence holds the mission instead of sending replacements.
func TradeMissionDepartureEvidence(m domain.TradeMission, read *bridge.WorldProgressionRead) (domain.TradeMissionPhase, *bridge.CaravanJourney, bool) {
	if m.Validate() != nil || m.Phase != domain.TradeMissionPlanned && m.Phase != domain.TradeMissionDeparting && m.Phase != domain.TradeMissionOutbound || read == nil || read.Context == nil || read.Context.Identity == nil || read.Context.Identity.GetColonyId() != string(m.HomeColony) {
		return m.Phase, nil, false
	}
	exact := func(ids []string) bool {
		return len(ids) == len(m.Crew) && !slices.ContainsFunc(m.Crew, func(id domain.PawnID) bool { return !slices.Contains(ids, string(id)) })
	}
	overlaps := func(ids []string) bool {
		return slices.ContainsFunc(m.Crew, func(id domain.PawnID) bool { return slices.Contains(ids, string(id)) })
	}
	var found *bridge.CaravanJourney
	for i := range read.Caravans {
		row := &read.Caravans[i]
		if overlaps(row.PawnIDs) {
			if !exact(row.PawnIDs) || found != nil {
				return m.Phase, nil, false
			}
			found = row
		}
	}
	if found != nil {
		if found.Tile == m.SettlementTile && !found.Moving {
			return domain.TradeMissionBuying, found, true
		}
		return domain.TradeMissionOutbound, found, true
	}
	for _, assembly := range read.Assemblies {
		if overlaps(assembly.PawnIDs) {
			if assembly.MapID != int32(m.HomeMap) || !exact(assembly.PawnIDs) {
				return m.Phase, nil, false
			}
			return domain.TradeMissionDeparting, nil, true
		}
	}
	return m.Phase, nil, false
}

// AdmitTradeMissionDeparture is deliberately unregistered. The complete driver
// calls it under the player gate after persisting the planned Project.
func (r *Rounder) AdmitTradeMissionDeparture(call, epoch context.Context, state ControlState, project store.ProjectState, read *bridge.WorldProgressionRead, arbiter *stepArbiter) (domain.PlanID, error) {
	m, err := domain.DecodeTradeMission(project.Project.Record)
	if err != nil {
		return "", err
	}
	if !state.Enabled || !state.ObservationKnown || !project.Project.Snapshot.SameWorld(state.Snapshot) || m.Phase != domain.TradeMissionPlanned || len(project.History) != 0 {
		return "", ErrControl
	}
	if !tradeMissionCurrentRead(state.Snapshot, read) {
		return "", ErrControl
	}
	if err = r.player.current(call, epoch); err != nil {
		return "", err
	}
	if r.player.session.State() != state {
		return "", ErrControl
	}
	if phase, _, observed := TradeMissionDepartureEvidence(m, read); observed {
		m.Phase = phase
		record, err := m.Record()
		if err != nil {
			return "", err
		}
		_, err = r.player.journal.RecordProject(call, project.Project.ID, project.Revision, record)
		return "", err
	}
	if !tradeMissionCrewAtHome(m, read) {
		return "", ErrControl
	}
	if !arbiter.tryClaim(m.Crew, "trade-mission:"+m.Settlement) {
		return "", errors.New("trade mission crew claimed")
	}
	d, err := m.Departure()
	if err != nil {
		return "", err
	}
	id := domain.MintPlanID()
	a, err := domain.NewCaravanDepartureAction(domain.ActionID(string(id)+"-0"), d)
	if err != nil {
		return "", err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{a})
	if err != nil {
		return "", err
	}
	m.Phase = domain.TradeMissionDeparting
	record, err := m.Record()
	if err != nil {
		return "", err
	}
	_, err = r.player.journal.CommitProjectMethodRecord(call, project.Project.ID, project.Revision, "trade-departure", fmt.Sprintf("settlement:%s", m.Settlement), plan, record)
	return id, err
}

func tradeMissionCurrentRead(world domain.GenerationSnapshot, read *bridge.WorldProgressionRead) bool {
	if read == nil || read.Context == nil || read.Context.Identity == nil {
		return false
	}
	id := read.Context.Identity
	return id.GetColonyId() == string(world.Colony) && id.GetMapId() == int32(world.Map) && id.GetLoadToken() == string(world.Load) && domain.NativeGeneration(read.Context.GetNativeGeneration()) == world.Native
}

func tradeMissionCrewAtHome(m domain.TradeMission, read *bridge.WorldProgressionRead) bool {
	for _, caravan := range read.Caravans {
		for _, id := range m.Crew {
			if slices.Contains(caravan.PawnIDs, string(id)) {
				return false
			}
		}
	}
	for _, assembly := range read.Assemblies {
		for _, id := range m.Crew {
			if slices.Contains(assembly.PawnIDs, string(id)) {
				return false
			}
		}
	}
	for _, home := range read.Maps {
		if home.Home && domain.MapID(home.ID) == m.HomeMap && home.Tile == m.HomeTile {
			return !slices.ContainsFunc(m.Crew, func(id domain.PawnID) bool { return !slices.Contains(home.PawnIDs, string(id)) })
		}
	}
	return false
}
