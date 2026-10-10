package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// exportMemory is what the latest declarations of one world derived for the
// sale path: the generic goods MaintainTrade ranks, and the gear above demand
// (apparel from the export declaration, weapons from the armory's). Derived
// state in memory only, empty until the first declaration after a restart; a
// weapon surplus can be one review stale when the armory declares after the
// trade selection reads it.
type exportMemory struct {
	mu           sync.Mutex
	snapshot     domain.GenerationSnapshot
	sale         []policy.Resource
	apparel      []policy.GearStock
	apparelKnown bool
	weapons      map[policy.Resource]int
	weaponsKnown bool
	gap          *policy.ExportView
}

func (m *exportMemory) world(snapshot domain.GenerationSnapshot) {
	if m.snapshot != snapshot {
		m.snapshot, m.sale, m.apparel, m.apparelKnown, m.weapons, m.weaponsKnown, m.gap = snapshot, nil, nil, false, nil, false, nil
	}
}

func (m *exportMemory) setPlan(snapshot domain.GenerationSnapshot, sale []policy.Resource, apparel []policy.GearStock, known bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world(snapshot)
	m.sale, m.apparel, m.apparelKnown = sale, apparel, known
}

// setView keeps the latest declaration's silver gap for the ledger view.
func (m *exportMemory) setView(snapshot domain.GenerationSnapshot, v *policy.ExportView) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world(snapshot)
	m.gap = v
}

// view is the silver gap of snapshot's world, nil when none was read in it.
func (m *exportMemory) view(snapshot domain.GenerationSnapshot) *policy.ExportView {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot {
		return nil
	}
	return m.gap
}

func (m *exportMemory) setWeapons(snapshot domain.GenerationSnapshot, spare map[policy.Resource]int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.world(snapshot)
	m.weapons, m.weaponsKnown = spare, true
}

// products are the generic export products of snapshot's world.
func (m *exportMemory) products(snapshot domain.GenerationSnapshot) []policy.Resource {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot {
		return nil
	}
	return slices.Clone(m.sale)
}

// surplus is the gear above demand of snapshot's world; ok is false while
// neither the apparel nor the weapon surplus is read.
func (m *exportMemory) surplus(snapshot domain.GenerationSnapshot) (policy.GearSurplus, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.snapshot != snapshot || !m.apparelKnown && !m.weaponsKnown {
		return policy.GearSurplus{}, false
	}
	out := policy.GearSurplus{}
	if m.apparelKnown {
		out.Apparel = slices.Clone(m.apparel)
	}
	if m.weaponsKnown {
		out.Weapons = map[policy.Resource]int{}
		for definition, n := range m.weapons {
			out.Weapons[definition] = n
		}
	}
	return out, true
}

// known is whether both halves of the gear surplus are read.
func (m *exportMemory) known(snapshot domain.GenerationSnapshot) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.snapshot == snapshot && m.apparelKnown && m.weaponsKnown
}

// exportSource is the native surface the export declaration reads beyond the
// review's projection.
type exportSource interface {
	observation.DefinitionSource
	ReadTradeAcquisition(context.Context, *c.Identity, *op.FormCaravanIntent) (*o.TradeAcquisition, bridge.Result, error)
	ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
	ReadWorld(context.Context, *c.Identity, int32, float64) (bridge.WorldRead, bridge.Result, error)
}

// RoundsTradeExportPlanner is MaintainTrade's planner. The export batches are
// the ledger's: DeclareOrders declares them (OrderDeclarer) and the planner
// commits no bill method of its own. What stays is the game time a batch
// takes, which a placed bill does not ask the clock for.
type RoundsTradeExportPlanner struct {
	reviewer *Rounder
	mu       sync.Mutex
	// working is whether the latest declaration carries an export order.
	working bool
}

// exportNativeWorkTicks is the window an export batch asks for per step; the
// review re-reads the bench between windows.
const exportNativeWorkTicks = 2500

// allSettlementsRadius reads every settlement of the world, whatever its distance.
const allSettlementsRadius = 1e9

func NewRoundsTradeExportPlanner(reviewer *Rounder) (*RoundsTradeExportPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsTradeExportPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsTradeExportPlanner{reviewer: reviewer}, nil
}

func (r *RoundsTradeExportPlanner) setWorking(v bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.working = v
}

// DeclareOrders is the declaration owned by policy.MaintainTrade.
func (r *RoundsTradeExportPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(policy.MaintainTrade), err
}

// declareOrders declares the export batches (OrderDeclarer) and files the sale
// path's inputs. Any unread input abstains: the ledger removes nothing.
func (r *RoundsTradeExportPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	r.setWorking(false)
	rd := r.reviewer
	review, err := rd.player.journal.LoadRounds(ctx)
	if err != nil {
		return abstainOnRead(ctx, policy.UnreadReview)
	}
	f := projection.Facts
	gap, demand := policy.RoundsSilverGap(f, rd.policy, review.Latches.MedicalReserve)
	request := policy.ExportRequest{
		Benches: benches, Profiles: domain.Unknown[[]policy.PawnProfile](), Items: f.Items,
		Gap: gap, Demand: demand, Floors: policy.RoundsTradeFloors(rd.policy, nil),
		Packed: domain.Unknown[int64](), Buys: map[policy.Resource]domain.Fact[bool]{},
		CashCap: domain.Unknown[int64](), Sources: map[policy.Resource][]policy.SupplyCandidate{},
	}
	if pawns, known := projection.WorkPawns.Value(); known {
		request.Profiles = domain.Known(policy.Profiles(pawns))
	}
	stock, stockKnown := exportStock(f)
	request.Stock = stock
	if review.Enabled && review.Snapshot == snapshot {
		request.Runways = review.ResourceRunwayState()
	}
	apparel, apparelKnown := policy.ApparelSurplus(policy.ClothingDemandInput{Garments: f.Garments, Categories: f.Items.StuffCategories, Gear: f.Gear, Stock: policy.StockReader{Resources: f.Resources, Wood: f.Wood}})
	if apparelKnown {
		request.Surplus.Apparel = apparel
	}
	if weapons, ok := rd.exports.surplus(snapshot); ok && rd.exports.known(snapshot) {
		request.Surplus.Weapons = weapons.Weapons
		request.SurplusKnown = apparelKnown
	}
	// The sale path learns the rankable goods even when this Round abstains.
	ranked := policy.DeclareExportOrders(request)
	rd.exports.setPlan(snapshot, policy.ExportSaleProducts(f.Items, ranked.Products), apparel, apparelKnown)
	rd.exports.setView(snapshot, policy.NewExportView(ranked, gap))
	if !stockKnown {
		return policy.Abstaining(policy.UnreadStock), nil
	}
	if g, known := gap.Value(); !known || !(g > 0) {
		return ranked.Declared, nil
	}
	native, ok := rd.native.(exportSource)
	resource := rd.resourceNative
	if resource == nil {
		resource, _ = rd.native.(RoundsResourceSource)
	}
	if !ok || resource == nil {
		return policy.Abstaining(policy.UnreadNativeRead), nil
	}
	identity := boundary.Identity(snapshot)
	catalog, err := native.DefinitionCatalog(ctx, identity)
	if err != nil || catalog == nil {
		return abstainOnRead(ctx, policy.UnreadDefinitions)
	}
	kinds, known := r.traderKinds(ctx, native, snapshot, identity)
	if !known {
		return abstainOnRead(ctx, policy.UnreadBuyers)
	}
	request.CashCap, request.Buys = exportBuyers(catalog, kinds, ranked.Products)
	request.WorkFor = exportWorkFor(catalog)
	names := policy.ExportIngredients(request)
	strs := make([]string, len(names))
	for i, n := range names {
		strs[i] = string(n)
	}
	if len(strs) > 0 {
		if request.Supply, _, err = resource.ReadSupplyStock(ctx, identity, strs); err != nil {
			return abstainOnRead(ctx, policy.UnreadSupply)
		}
	}
	center, centered := projection.Center().Value()
	acquisition, _ := projection.Acquisition.Value()
	for _, name := range names {
		rows, _, _, err := resource.ReadResourceSources(ctx, identity, string(name))
		if err != nil {
			return abstainOnRead(ctx, policy.UnreadSources)
		}
		var mines []policy.ResourceSource
		for _, row := range rows {
			if row.Method == policy.ResourceSourceMine && !row.Taken && policy.MineSafe(row.Safety) {
				mines = append(mines, row)
			}
		}
		sources := policy.MineCandidates(name, mines, domain.Unknown[int64]())
		if centered {
			var hand []policy.AcquisitionSource
			for _, s := range acquisition {
				if policy.Resource(s.Resource) == name {
					hand = append(hand, s)
				}
			}
			sources = append(sources, policy.AcquisitionSourceCandidates(name, hand, center, domain.Unknown[int64]())...)
		}
		request.Sources[name] = sources
	}
	if packed, perr := r.packedArt(ctx, snapshot, identity, projection); perr == nil {
		request.Packed = packed
	}
	plan := policy.DeclareExportOrders(request)
	rd.exports.setView(snapshot, policy.NewExportView(plan, gap))
	r.setWorking(len(plan.Declared.Orders) > 0)
	return plan.Declared, nil
}

// exportWorkFor reads a product's WorkToMake under a stuff from the catalog.
func exportWorkFor(catalog *bridge.DefinitionCatalog) func(product, stuff policy.Resource) (float64, bool) {
	return func(product, stuff policy.Resource) (float64, bool) {
		return catalog.WorkToMake(string(product), string(stuff))
	}
}

// exportStock is the colony stock by definition from the resource census.
func exportStock(f policy.RoundsFacts) (map[policy.Resource]int64, bool) {
	rows, known := f.Resources.Value()
	if !known {
		return nil, false
	}
	out := map[policy.Resource]int64{}
	for _, row := range rows {
		out[row.Resource] += row.Count
	}
	return out, true
}

// packedArt counts the packed sculptures no owed room reserves.
func (r *RoundsTradeExportPlanner) packedArt(ctx context.Context, snapshot domain.GenerationSnapshot, identity *c.Identity, projection observation.ColonyProjection) (domain.Fact[int64], error) {
	native, ok := r.reviewer.native.(packedSource)
	if !ok {
		return domain.Unknown[int64](), nil
	}
	if _, known := projection.Rooms.Value(); !known {
		return domain.Unknown[int64](), nil
	}
	sale, err := saleSculptures(ctx, native, identity, projection)
	if err != nil || sale == nil {
		return domain.Unknown[int64](), err
	}
	return domain.Known(int64(len(sale))), nil
}

// traderKinds are the trader kinds the colony can reach: the orbital and
// caravan requests the comms consoles offer and the known settlements'.
func (r *RoundsTradeExportPlanner) traderKinds(ctx context.Context, native exportSource, snapshot domain.GenerationSnapshot, identity *c.Identity) ([]string, bool) {
	facts, _, err := native.ReadTradeAcquisition(ctx, identity, nil)
	if err != nil || facts == nil {
		return nil, false
	}
	seen := map[string]bool{}
	for _, row := range facts.Requests {
		if row.Eligible != nil && !row.GetEligible() {
			continue
		}
		if kind := row.GetTraderKind(); kind != "" {
			seen[kind] = true
		}
	}
	progress, _, err := native.ReadWorldProgression(ctx, identity, false)
	if err != nil {
		return nil, false
	}
	home := int32(-1)
	for _, row := range progress.Maps {
		if row.Home && domain.MapID(row.ID) == snapshot.Map {
			home = row.Tile
		}
	}
	if home >= 0 {
		world, _, err := native.ReadWorld(ctx, identity, home, allSettlementsRadius)
		if err != nil {
			return nil, false
		}
		for _, s := range world.Settlements {
			if s.TraderKind != "" {
				seen[s.TraderKind] = true
			}
		}
	}
	out := make([]string, 0, len(seen))
	for kind := range seen {
		out = append(out, kind)
	}
	slices.Sort(out)
	return out, true
}

// exportBuyers evaluates the reachable kinds' buy generators: per product
// whether any kind buys it, and the cash cap, the low end of the silver the
// best kind carries. No reachable kind buys nothing and caps at zero.
func exportBuyers(catalog *bridge.DefinitionCatalog, kinds []string, products []policy.Resource) (domain.Fact[int64], map[policy.Resource]domain.Fact[bool]) {
	buys := map[policy.Resource]domain.Fact[bool]{}
	for _, product := range products {
		any, unknown := false, false
		for _, kind := range kinds {
			v, known := catalog.TraderKindBuys(kind, product).Value()
			switch {
			case !known:
				unknown = true
			case v:
				any = true
			}
		}
		switch {
		case any:
			buys[product] = domain.Known(true)
		case unknown:
			buys[product] = domain.Unknown[bool]()
		default:
			buys[product] = domain.Known(false)
		}
	}
	cap, unknown := int64(0), false
	for _, kind := range kinds {
		silver, known := catalog.TraderKindSilver(kind).Value()
		if !known {
			unknown = true
			continue
		}
		cap = max(cap, silver)
	}
	if cap == 0 && unknown {
		return domain.Unknown[int64](), buys
	}
	return domain.Known(cap), buys
}

func (r *RoundsTradeExportPlanner) step(call, _ context.Context, _ *stepArbiter) (RoundsBillResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBillResult{Verdict: BuildingReasonDisabled}, nil
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsBillResult{Verdict: BuildingReasonNoReview}, nil
	}
	_, workable, err := p.journal.WorkableOwner(call, review, policy.MaintainTrade)
	if err != nil {
		return RoundsBillResult{}, err
	}
	r.mu.Lock()
	working := r.working
	r.mu.Unlock()
	if !workable || !working {
		return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	return RoundsBillResult{Verdict: waitFor(WaitExistingWork, "export"), NativeWorkTicks: exportNativeWorkTicks}, nil
}
