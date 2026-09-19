package policy

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type PowerSite struct {
	ID, Definition string
	Cell           domain.Cell
	Occupied       []domain.Cell
	PowerBuilding
}

type PowerTopology struct {
	Buildings      []PowerSite
	Conduits       []domain.Cell
	UnsafeConduits []domain.Cell
	Blackout       domain.Fact[bool]
	// Eclipse is the native eclipse condition: solar output is zero for the
	// rest of it, so the day's budget carries no solar energy.
	Eclipse domain.Fact[bool]
	// Networks is the per-net energy summary; an empty census means the native
	// read predates network facts and reserve runway stays unknown.
	Networks []PowerNetworkFact
	// Geysers is the native steam-geyser census: every geyser on the map with
	// its footprint and whether a harvester or another building already
	// stands on it. A free geyser in reach hosts a geothermal generator.
	Geysers []PowerGeyser
}

// PowerGeyser is one steam geyser as the census reports it.
type PowerGeyser struct {
	ID       string
	Cell     domain.Cell
	Cells    []domain.Cell
	Occupied bool
}

type PowerMethod string

const (
	PowerUnknown      PowerMethod = "unknown"
	PowerNoMethod     PowerMethod = "no_deficit"
	PowerWaitOutput   PowerMethod = "waiting_for_native_power"
	PowerWaitFuel     PowerMethod = "waiting_for_refuel"
	PowerWaitRepair   PowerMethod = "waiting_for_repair"
	PowerWaitBlackout PowerMethod = "solar_flare"
	PowerWaitPlayer   PowerMethod = "player_disabled_power"
	PowerRouteBlocked PowerMethod = "no_observed_route"
	PowerNoGenerator  PowerMethod = "no_affordable_generator"
	PowerConnect      PowerMethod = "HiddenConduit"
	PowerGenerate     PowerMethod = "generate"
	PowerShelter      PowerMethod = "shelter_power"
	PowerWaitCharge   PowerMethod = "waiting_for_charge"
	PowerStore        PowerMethod = "store"
)

// GeneratorOption is one generator definition the planner may compile, in
// preference order. Fuel names the resource a refuelable generator burns and
// FuelStock the observed colony stock of it; a fuel-free generator leaves both
// empty. Availability comes from native planning definitions (research and
// content), never from an assumption about the installed game.
type GeneratorOption struct {
	Definition  string
	Available   domain.Fact[bool]
	Fuel        Resource
	FuelStock   domain.Fact[int64]
	MinimumFuel int64
}

// GeneratorDefinitions lists every generator definition RankGenerators may
// choose, in the order it prefers them within a tier. Planners request these
// planning definitions and Hands correlate completed work against them. The
// wind turbine follows the solar generator: both are fuel-free, but a turbine
// needs an unobstructed catch zone the placement preview reports, so a site
// that has none falls back to the next ranked definition.
var GeneratorDefinitions = []string{"SolarGenerator", WindTurbineDefinition, "WoodFiredGenerator", "ChemfuelPoweredGenerator"}

// WindTurbineDefinition is the ranked generator whose placement preview
// must report a clear catch zone.
const WindTurbineDefinition = "WindTurbine"

// GeothermalDefinition is the generator a free steam geyser hosts. It is not
// ranked: SelectPowerMethod proposes it ahead of every other generator when a
// free geyser is in reach of the draining consumer.
const GeothermalDefinition = "GeothermalGenerator"

// GeothermalReachCells bounds the conduit route from a draining consumer to a
// geyser the family will build on: longer routes are left to a nearer
// generator.
const GeothermalReachCells = 48

// PowerFamilyDefinitions lists every definition the power family compiles:
// the conduit, each generator, the geothermal generator and the battery.
func PowerFamilyDefinitions() []string {
	return append(append([]string{string(PowerConnect)}, GeneratorDefinitions...), GeothermalDefinition, BatteryDefinition)
}

// DefaultGeneratorOptions pairs each generator definition with its fuel and the
// stock floor below which the planner will not commit to that fuel.
func DefaultGeneratorOptions(available func(string) domain.Fact[bool], stock func(Resource) domain.Fact[int64]) []GeneratorOption {
	fuels := map[string]struct {
		fuel    Resource
		minimum int64
	}{"WoodFiredGenerator": {"WoodLog", 75}, "ChemfuelPoweredGenerator": {"Chemfuel", 30}}
	options := make([]GeneratorOption, 0, len(GeneratorDefinitions))
	for _, name := range GeneratorDefinitions {
		option := GeneratorOption{Definition: name, Available: available(name)}
		if f, ok := fuels[name]; ok {
			option.Fuel, option.MinimumFuel, option.FuelStock = f.fuel, f.minimum, stock(f.fuel)
		}
		options = append(options, option)
	}
	return options
}

// PowerPlanning carries the planner-side choices SelectPowerMethod needs
// beyond the observed topology: which generators are compilable, whether a
// battery is, how many days of stored reserve a draining network must keep
// before the budget is consulted even though every consumer is currently
// powered, and the margin the storage target carries over the night deficit.
type PowerPlanning struct {
	Generators          []GeneratorOption
	BatteryAvailable    domain.Fact[bool]
	GeothermalAvailable domain.Fact[bool]
	ReserveMinDays      float64
	StorageMargin       float64
	// PendingDemandW is the wattage of consumers other goals' admitted plans
	// are about to build, counted in the budget's demand so generation and
	// storage are sized for the colony being built, not only the one standing.
	PendingDemandW float64
}

func DefaultPowerPlanning() PowerPlanning {
	return PowerPlanning{ReserveMinDays: 1, StorageMargin: 0.25}
}

// GeneratorRanking is what RankGenerators knows about the colony beyond the
// options: whether a battery can be built (a renewable is free to run but
// needs storage to carry the night) and whether the shortfall being closed
// is night-only, which a solar generator cannot touch.
type GeneratorRanking struct {
	BatteryAvailable domain.Fact[bool]
	NightOnly        bool
}

// RankGenerators orders the known-available options by cost per delivered
// day of energy under current stock: a fuel-free generator first once a
// battery can bank its surplus, fuel-burning generators whose stock meets
// the floor next (list order breaks ties: wood before chemfuel, since
// MaintainWood replenishes it), fuel-short ones after, and a renewable that
// cannot serve the deficit (no battery, or a night-only shortfall) last,
// still chosen when nothing else is available.
func RankGenerators(options []GeneratorOption, ranking GeneratorRanking) []string {
	type ranked struct {
		definition  string
		tier, order int
	}
	var rows []ranked
	battery, _ := ranking.BatteryAvailable.Value()
	for i, o := range options {
		if available, ok := o.Available.Value(); !ok || !available {
			continue
		}
		tier := 0
		switch profile := SourceProfile(o.Definition); {
		case profile.Night < 1 && (!battery || ranking.NightOnly && profile.Night == 0):
			tier = 3
		case profile.Night < 1:
			tier = 0
		case o.Fuel == "":
			tier = 1
		default:
			tier = 1
			if stock, ok := o.FuelStock.Value(); ok && stock < o.MinimumFuel {
				tier = 2
			}
		}
		rows = append(rows, ranked{o.Definition, tier, i})
	}
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].tier != rows[j].tier {
			return rows[i].tier < rows[j].tier
		}
		return rows[i].order < rows[j].order
	})
	out := make([]string, 0, len(rows))
	for _, r := range rows {
		out = append(out, r.definition)
	}
	return out
}

// SelectGenerator is the first generator RankGenerators returns. With no
// options at all the legacy wood-fired default applies; with options but
// none available the result is empty and the caller reports
// PowerNoGenerator.
func SelectGenerator(options []GeneratorOption, ranking GeneratorRanking) string {
	if len(options) == 0 {
		return "WoodFiredGenerator"
	}
	if ranked := RankGenerators(options, ranking); len(ranked) > 0 {
		return ranked[0]
	}
	return ""
}

type PowerProposal struct {
	Method     PowerMethod
	Definition string
	Key        domain.MethodID
	Target     string
	Center     domain.Cell
	Cells      []domain.Cell
	Room       Rectangle
	// Alternatives lists the generators ranked after Definition for a
	// generate method, so a definition no site accepts (a wind turbine with
	// every catch zone obstructed) yields to the next one under the same key.
	Alternatives []string
	// Budget is the target network's 24 h balance behind a generate, store
	// or charge decision; zero for the connect and hold methods.
	Budget PowerBudget
}

// FixedSite reports whether the proposal names its exact placement: a
// geothermal generator is anchored on its geyser (Center) rather than
// searched for near the consumer.
func (p PowerProposal) FixedSite() bool {
	return p.Method == PowerGenerate && p.Definition == GeothermalDefinition
}

// SelectPowerMethod ports the network-local capacity and bounded route choices.
// Proposed geometry still requires native placement previews and shared admission.
// Output and PowerOn, not installed capacity or a receipt, establish recovery.
// Producers that are out of fuel or broken down hold the proposal: refueling
// and repair are ordinary pawn work owned by other families. A powered network
// whose stored reserve runway falls under ReserveMinDays is a deficit too,
// sized by its 24 h budget (ComputePowerBudget): a generation shortfall adds a
// generator, a storage shortfall alone adds a battery, and a network the
// budget already covers waits for its bank to charge.
func SelectPowerMethod(fact domain.Fact[PowerTopology], bounds Bounds, cells []SiteCell, protected []domain.Cell, planning PowerPlanning) (PowerProposal, error) {
	v, known := fact.Value()
	if !known {
		return PowerProposal{Method: PowerUnknown}, nil
	}
	if !foodNumber(planning.ReserveMinDays) || !foodNumber(planning.StorageMargin) || !foodNumber(planning.PendingDemandW) || planning.StorageMargin > 10 || planning.PendingDemandW > 1e12 || len(planning.Generators) > 64 {
		return PowerProposal{}, errors.New("invalid power planning")
	}
	if len(v.Networks) > 4096 || len(v.Geysers) > 256 {
		return PowerProposal{}, errors.New("invalid power network census")
	}
	networks := map[string]PowerNetworkFact{}
	for _, n := range v.Networks {
		if !foodID(n.ID) {
			return PowerProposal{}, errors.New("invalid power network identity")
		}
		if _, dup := networks[n.ID]; dup {
			return PowerProposal{}, errors.New("duplicate power network")
		}
		for _, w := range []domain.Fact[float64]{n.GenerationW, n.ConsumptionW, n.StoredWD, n.CapacityWD} {
			if x, k := w.Value(); k && (math.IsNaN(x) || math.IsInf(x, 0) || x < 0 || x > 1e12) {
				return PowerProposal{}, errors.New("invalid power network energy")
			}
		}
		networks[n.ID] = n
	}
	if bounds.Width < 1 || bounds.Height < 1 || bounds.Width > 4096 || bounds.Height > 4096 || len(v.Buildings)+len(v.Conduits) > 256 || len(cells) > 65536 || len(protected) > 65536 {
		return PowerProposal{}, errors.New("invalid power planning bounds")
	}
	inside := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height }
	seen := map[string]bool{}
	complete := true
	for _, b := range v.Buildings {
		if !foodID(b.ID) || !foodID(b.Definition) || seen[b.ID] || !inside(b.Cell) || len(b.Occupied) < 1 || len(b.Occupied) > 4096 {
			return PowerProposal{}, errors.New("invalid power building geometry")
		}
		seen[b.ID] = true
		footprint := map[domain.Cell]bool{}
		for _, c := range b.Occupied {
			if !inside(c) || footprint[c] {
				return PowerProposal{}, errors.New("invalid power footprint")
			}
			footprint[c] = true
		}
		if !footprint[b.Cell] {
			return PowerProposal{}, errors.New("power anchor outside footprint")
		}
		for _, watts := range []domain.Fact[float64]{b.BaseW, b.OutputW} {
			w, k := watts.Value()
			if k && (math.IsNaN(w) || math.IsInf(w, 0) || math.Abs(w) > 1e12) {
				return PowerProposal{}, errors.New("invalid power wattage")
			}
			complete = complete && k
		}
		for _, amount := range []domain.Fact[float64]{b.Fuel, b.TargetFuel, b.Stored, b.Capacity} {
			if x, k := amount.Value(); k && (math.IsNaN(x) || math.IsInf(x, 0) || x < 0 || x > 1e12) {
				return PowerProposal{}, errors.New("invalid power service amount")
			}
		}
		if len(b.FuelDefinitions) > 256 {
			return PowerProposal{}, errors.New("invalid power fuel definitions")
		}
		connected, ck := b.Connected.Value()
		net, nk := b.Network.Value()
		if nk && (!foodID(net) || ck && !connected) {
			return PowerProposal{}, errors.New("invalid power network")
		}
		_, pk := b.Powered.Value()
		_, fk := b.Forbidden.Value()
		_, sk := b.SwitchedOn.Value()
		complete = complete && ck && (!connected || nk) && pk && fk && sk
	}
	existing := map[domain.Cell]bool{}
	for _, c := range v.Conduits {
		if !inside(c) || existing[c] {
			return PowerProposal{}, errors.New("invalid conduit census")
		}
		existing[c] = true
	}
	unsafeSeen := map[domain.Cell]bool{}
	for _, c := range v.UnsafeConduits {
		if !existing[c] || unsafeSeen[c] {
			return PowerProposal{}, errors.New("invalid unsafe conduit census")
		}
		unsafeSeen[c] = true
	}
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		if !inside(c) {
			return PowerProposal{}, errors.New("invalid protected power cell")
		}
		blocked[c] = true
	}
	geyserSeen := map[string]bool{}
	for _, g := range v.Geysers {
		if !foodID(g.ID) || geyserSeen[g.ID] || !inside(g.Cell) || len(g.Cells) < 1 || len(g.Cells) > 64 {
			return PowerProposal{}, errors.New("invalid steam geyser census")
		}
		geyserSeen[g.ID] = true
		anchored := false
		for _, c := range g.Cells {
			if !inside(c) {
				return PowerProposal{}, errors.New("invalid steam geyser footprint")
			}
			anchored = anchored || c == g.Cell
		}
		if !anchored {
			return PowerProposal{}, errors.New("steam geyser anchor outside footprint")
		}
	}
	route := map[domain.Cell]bool{}
	seenCells := map[domain.Cell]bool{}
	for _, c := range cells {
		if !inside(c.Cell) || seenCells[c.Cell] {
			return PowerProposal{}, errors.New("invalid power route census")
		}
		seenCells[c.Cell] = true
		route[c.Cell] = positive(c.SupportsLight) && !blocked[c.Cell]
	}
	blackout, known := v.Blackout.Value()
	eclipse, _ := v.Eclipse.Value()
	if !known || !complete {
		return PowerProposal{Method: PowerUnknown}, nil
	}
	if blackout {
		return PowerProposal{Method: PowerWaitBlackout}, nil
	}
	// Protect equipment before energizing or expanding the network. The native
	// census discovers vulnerable definitions; weather need not already be wet.
	for _, b := range v.Buildings {
		if !positive(b.RainVulnerable) || positive(b.Roofed) {
			continue
		}
		if forbidden, known := b.Forbidden.Value(); !known || forbidden {
			return PowerProposal{Method: PowerWaitPlayer}, nil
		}
		if _, known := b.Roofed.Value(); !known {
			return PowerProposal{Method: PowerUnknown}, nil
		}
		room, ok := powerShelter(b, cells, blocked, bounds)
		if !ok {
			return PowerProposal{Method: PowerRouteBlocked}, nil
		}
		return PowerProposal{Method: PowerShelter, Target: b.ID, Center: b.Cell, Room: room, Key: powerMethodKey("shelter", b.ID+fmt.Sprint(room))}, nil
	}
	if len(v.UnsafeConduits) > 0 {
		upgrade := append([]domain.Cell(nil), v.UnsafeConduits...)
		sort.Slice(upgrade, func(i, j int) bool { return cellLess(upgrade[i], upgrade[j]) })
		upgrade = upgrade[:min(len(upgrade), 8)]
		return PowerProposal{Method: PowerConnect, Cells: upgrade, Key: powerMethodKey("upgrade", fmt.Sprint(upgrade))}, nil
	}
	var producers []PowerSite
	for _, b := range v.Buildings {
		w, _ := b.BaseW.Value()
		if w > 0 {
			producers = append(producers, b)
		}
	}
	result := PowerProposal{Method: PowerNoMethod}
	for _, target := range v.Buildings {
		base, _ := target.BaseW.Value()
		if base >= 0 {
			continue
		}
		forbidden, _ := target.Forbidden.Value()
		switched, _ := target.SwitchedOn.Value()
		if forbidden || !switched {
			result.Method = PowerWaitPlayer
			continue
		}
		connected, _ := target.Connected.Value()
		powered, _ := target.Powered.Value()
		network, _ := target.Network.Value()
		same := func(b PowerSite) bool {
			n, _ := b.Network.Value()
			c, _ := b.Connected.Value()
			return connected && c && n == network
		}
		demand, capacity, output := 0.0, 0.0, 0.0
		connectedProducers, disabledProducer, unfueledProducer, brokenProducer := 0, false, false, false
		budget := PowerBudgetInput{Eclipse: eclipse, StorageMargin: planning.StorageMargin}
		for _, b := range v.Buildings {
			if !same(b) {
				continue
			}
			w, _ := b.BaseW.Value()
			actual, _ := b.OutputW.Value()
			output += actual
			if capacity, ok := b.Capacity.Value(); ok {
				budget.CapacityWD += capacity
			}
			if w < 0 {
				demand -= w
			}
			if w > 0 {
				connectedProducers++
				capacity += w
				budget.Producers = append(budget.Producers, PowerProducer{Definition: b.Definition, NominalW: w})
				f, _ := b.Forbidden.Value()
				s, _ := b.SwitchedOn.Value()
				disabledProducer = disabledProducer || f || !s
				empty, _ := b.OutOfFuel.Value()
				broken, _ := b.BrokenDown.Value()
				unfueledProducer = unfueledProducer || empty
				brokenProducer = brokenProducer || broken
			}
		}
		budget.DemandW = demand + planning.PendingDemandW
		// Reserve runway only matters while the network is powered: a
		// draining battery keeps consumers on until it does not, and a
		// network whose producers cannot carry the coming night (solar by
		// day, no bank) is short of reserve before the sun goes down.
		reserveShort := false
		if connected && powered {
			if net, ok := networks[network]; ok {
				if days, known := net.ReserveDays().Value(); known && days < planning.ReserveMinDays {
					reserveShort = true
				}
				if stored, known := net.StoredWD.Value(); known {
					if night := ComputePowerBudget(budget).NightDeficitWD; night > 0 && stored < night {
						reserveShort = true
					}
				}
			}
		}
		if connected && powered && output >= 0 && !reserveShort {
			continue
		}
		p := PowerProposal{Target: target.ID, Center: target.Cell}
		if disabledProducer {
			p.Method = PowerWaitPlayer
			return p, nil
		}
		if connectedProducers > 0 && capacity >= demand {
			switch {
			case brokenProducer:
				p.Method = PowerWaitRepair
			case unfueledProducer:
				p.Method = PowerWaitFuel
			case !powered || reserveShort:
				// Installed capacity covers demand on paper but the network is
				// draining its reserve or has already lost the consumer
				// (night-time solar, part-time generation): size it by the
				// budget of a day. A network the budget covers waits for its
				// bank to charge, or for native output to reach a consumer
				// still off.
				p.Budget = ComputePowerBudget(budget)
				switch {
				case p.Budget.GenerationShortfallW > 0:
					return generate(p, target, producers, planning, v.Geysers, route)
				case p.Budget.StorageShortfallWD > 0:
					return store(p, target, producers, planning, v.Geysers, route)
				}
				p.Method = PowerWaitCharge
				if !powered {
					p.Method = PowerWaitOutput
				}
			default:
				p.Method = PowerWaitOutput
			}
			return p, nil
		}
		if len(producers) > 0 && connectedProducers == 0 {
			destinations := map[domain.Cell]bool{}
			for _, b := range producers {
				f, _ := b.Forbidden.Value()
				s, _ := b.SwitchedOn.Value()
				if f || !s {
					continue
				}
				destinations[b.Cell] = true
				for _, c := range b.Occupied {
					existing[c] = true
				}
			}
			if len(destinations) == 0 {
				p.Method = PowerWaitPlayer
				return p, nil
			}
			path := powerRoute(target.Cell, destinations, route)
			if len(path) == 0 {
				p.Method = PowerRouteBlocked
				return p, nil
			}
			for _, c := range path {
				if !existing[c] {
					p.Cells = append(p.Cells, c)
				}
				if len(p.Cells) == 8 {
					break
				}
			}
			if len(p.Cells) == 0 {
				p.Method = PowerWaitOutput
				return p, nil
			}
			p.Method = PowerConnect
			p.Key = powerMethodKey("connect", fmt.Sprint(p.Cells))
			return p, nil
		}
		p.Budget = ComputePowerBudget(budget)
		return generate(p, target, producers, planning, v.Geysers, route)
	}
	return result, nil
}

// A narrow, supported enclosure leaves an aisle around the equipment and a
// doorway. Occupied, zoned, protected or unobserved perimeter cells fail closed.
func powerShelter(target PowerSite, cells []SiteCell, protected map[domain.Cell]bool, bounds Bounds) (Rectangle, bool) {
	minX, maxX, minZ, maxZ := target.Cell.X, target.Cell.X, target.Cell.Z, target.Cell.Z
	occupied := map[domain.Cell]bool{}
	for _, c := range target.Occupied {
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
		occupied[c] = true
	}
	r := Rectangle{X: minX - 2, Z: minZ - 2, Width: maxX - minX + 5, Height: maxZ - minZ + 5}
	if r.X < 0 || r.Z < 0 || r.X+r.Width > bounds.Width || r.Z+r.Height > bounds.Height || r.Width > 10 || r.Height > 10 {
		return Rectangle{}, false
	}
	observed := map[domain.Cell]SiteCell{}
	for _, c := range cells {
		observed[c.Cell] = c
	}
	for _, p := range rectCells(r) {
		if protected[p] {
			return Rectangle{}, false
		}
		if occupied[p] {
			continue
		}
		c, exists := observed[p]
		if !exists || !positive(c.Walkable) || !positive(c.SupportsLight) || !positive(measured(c.Occupied, func(v bool) bool { return !v })) || !positive(measured(c.Zone, func(v bool) bool { return !v })) {
			return Rectangle{}, false
		}
	}
	return r, true
}

// store proposes one battery for target's network when the budget is short
// of storage but not generation. A battery is never the answer to a
// generation shortfall; without a compilable battery the generation path
// adds a generator instead, which the observed reserve still justifies.
func store(p PowerProposal, target PowerSite, producers []PowerSite, planning PowerPlanning, geysers []PowerGeyser, route map[domain.Cell]bool) (PowerProposal, error) {
	if available, ok := planning.BatteryAvailable.Value(); !ok || !available {
		return generate(p, target, producers, planning, geysers, route)
	}
	ids := make([]string, 0, len(producers))
	for _, b := range producers {
		ids = append(ids, b.ID)
	}
	sort.Strings(ids)
	p.Method, p.Definition = PowerStore, BatteryDefinition
	p.Key = powerMethodKey("store", fmt.Sprintf("%s/%s/%d", target.ID, strings.Join(ids, "/"), int(p.Budget.StorageNeededWD-p.Budget.StorageShortfallWD)))
	return p, nil
}

// generate proposes one more generator for the network of target. Native
// producer identity, rather than fluctuating output, distinguishes an
// additional capacity proposal from replay of the same deficit. A free steam
// geyser within GeothermalReachCells of the consumer over routeable cells
// hosts a geothermal generator ahead of every ranked option: it runs around
// the clock on no fuel.
func generate(p PowerProposal, target PowerSite, producers []PowerSite, planning PowerPlanning, geysers []PowerGeyser, route map[domain.Cell]bool) (PowerProposal, error) {
	ids := make([]string, 0, len(producers))
	for _, b := range producers {
		ids = append(ids, b.ID)
	}
	sort.Strings(ids)
	p.Key = powerMethodKey("generate", target.ID+"/"+strings.Join(ids, "/"))
	if geyser, ok := reachableGeyser(target.Cell, geysers, planning, route); ok {
		p.Method, p.Definition = PowerGenerate, GeothermalDefinition
		p.Center, p.Cells = geyser.Cell, append([]domain.Cell(nil), geyser.Cells...)
		return p, nil
	}
	// A shortfall the day already covers is night-only: no solar answers it.
	ranking := GeneratorRanking{BatteryAvailable: planning.BatteryAvailable, NightOnly: p.Budget.GenerationShortfallW > 0 && p.Budget.DaySurplusWD > 0}
	if len(planning.Generators) == 0 {
		p.Method, p.Definition = PowerGenerate, SelectGenerator(nil, ranking)
		return p, nil
	}
	ranked := RankGenerators(planning.Generators, ranking)
	if len(ranked) == 0 {
		p.Method = PowerNoGenerator
		return p, nil
	}
	p.Method, p.Definition, p.Alternatives = PowerGenerate, ranked[0], ranked[1:]
	return p, nil
}

// reachableGeyser is the nearest free geyser a conduit route of at most
// GeothermalReachCells can reach from start, when the geothermal generator
// is natively available.
func reachableGeyser(start domain.Cell, geysers []PowerGeyser, planning PowerPlanning, route map[domain.Cell]bool) (PowerGeyser, bool) {
	if available, ok := planning.GeothermalAvailable.Value(); !ok || !available {
		return PowerGeyser{}, false
	}
	destinations := map[domain.Cell]PowerGeyser{}
	allowed := map[domain.Cell]bool{}
	for c, ok := range route {
		allowed[c] = ok
	}
	for _, g := range geysers {
		if g.Occupied {
			continue
		}
		for _, c := range g.Cells {
			destinations[c] = g
			allowed[c] = true
		}
	}
	if len(destinations) == 0 {
		return PowerGeyser{}, false
	}
	targets := map[domain.Cell]bool{}
	for c := range destinations {
		targets[c] = true
	}
	// The consumer stands on its own footprint, which the census reports
	// occupied: the search starts from it regardless.
	allowed[start] = true
	path := powerRoute(start, targets, allowed)
	if len(path) == 0 || len(path)-1 > GeothermalReachCells {
		return PowerGeyser{}, false
	}
	return destinations[path[0]], true
}

func powerMethodKey(prefix, identity string) domain.MethodID {
	digest := sha256.Sum256([]byte(identity))
	return domain.MethodID(fmt.Sprintf("%s-%x", prefix, digest[:12]))
}

// powerRouteBound caps the cells one route search visits: enough to reach a
// geyser GeothermalReachCells away across open ground.
const powerRouteBound = 16384

// The generator-to-consumer order allows successive eight-cell methods to
// extend one route. Unknown terrain and protected cells never become routes.
func powerRoute(start domain.Cell, destinations, allowed map[domain.Cell]bool) []domain.Cell {
	if !allowed[start] {
		return nil
	}
	queue := []domain.Cell{start}
	previous := map[domain.Cell]domain.Cell{start: start}
	for i := 0; i < len(queue); i++ {
		c := queue[i]
		if destinations[c] {
			path := []domain.Cell{c}
			for c != start {
				c = previous[c]
				path = append(path, c)
			}
			return path
		}
		for _, next := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
			if _, found := previous[next]; allowed[next] && !found {
				if len(previous) >= powerRouteBound {
					return nil
				}
				previous[next] = c
				queue = append(queue, next)
			}
		}
	}
	return nil
}
