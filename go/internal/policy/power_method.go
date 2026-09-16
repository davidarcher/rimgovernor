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
	Buildings []PowerSite
	Conduits  []domain.Cell
	Blackout  domain.Fact[bool]
	// Networks is the per-net energy summary; an empty census means the native
	// read predates network facts and reserve runway stays unknown.
	Networks []PowerNetworkFact
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
	PowerConnect      PowerMethod = "PowerConduit"
	PowerGenerate     PowerMethod = "generate"
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

// GeneratorDefinitions lists every generator definition the power family may
// compile, in the order SelectGenerator prefers them. Planners request these
// planning definitions and Hands correlate completed work against them.
var GeneratorDefinitions = []string{"SolarGenerator", "WoodFiredGenerator", "ChemfuelPoweredGenerator"}

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
// beyond the observed topology: which generators are compilable and how many
// days of stored reserve a draining network must keep before more generation
// is proposed even though every consumer is currently powered.
type PowerPlanning struct {
	Generators     []GeneratorOption
	ReserveMinDays float64
}

func DefaultPowerPlanning() PowerPlanning { return PowerPlanning{ReserveMinDays: 1} }

// SelectGenerator returns the first known-available option whose fuel stock
// (if any) is not known to be under its floor; when every available option is
// short of fuel the first available one is still chosen, since MaintainWood
// and ordinary hauling replenish fuel after construction. With no options at
// all the legacy wood-fired default applies; with options but none available
// the result is empty and the caller reports PowerNoGenerator.
func SelectGenerator(options []GeneratorOption) string {
	if len(options) == 0 {
		return "WoodFiredGenerator"
	}
	fallback := ""
	for _, o := range options {
		if available, ok := o.Available.Value(); !ok || !available {
			continue
		}
		if fallback == "" {
			fallback = o.Definition
		}
		if o.Fuel != "" {
			if stock, ok := o.FuelStock.Value(); ok && stock < o.MinimumFuel {
				continue
			}
		}
		return o.Definition
	}
	return fallback
}

type PowerProposal struct {
	Method     PowerMethod
	Definition string
	Key        domain.MethodID
	Target     string
	Center     domain.Cell
	Cells      []domain.Cell
}

// SelectPowerMethod ports the network-local capacity and bounded route choices.
// Proposed geometry still requires native placement previews and shared admission.
// Output and PowerOn, not installed capacity or a receipt, establish recovery.
// Producers that are out of fuel or broken down hold the proposal: refueling
// and repair are ordinary pawn work owned by other families. A powered network
// whose stored reserve runway falls under ReserveMinDays is a deficit too, so
// generation is sized to actual connected load rather than momentary surplus.
func SelectPowerMethod(fact domain.Fact[PowerTopology], bounds Bounds, cells []SiteCell, protected []domain.Cell, planning PowerPlanning) (PowerProposal, error) {
	v, known := fact.Value()
	if !known {
		return PowerProposal{Method: PowerUnknown}, nil
	}
	if math.IsNaN(planning.ReserveMinDays) || math.IsInf(planning.ReserveMinDays, 0) || planning.ReserveMinDays < 0 || len(planning.Generators) > 64 {
		return PowerProposal{}, errors.New("invalid power planning")
	}
	if len(v.Networks) > 4096 {
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
	blocked := map[domain.Cell]bool{}
	for _, c := range protected {
		if !inside(c) {
			return PowerProposal{}, errors.New("invalid protected power cell")
		}
		blocked[c] = true
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
	if !known || !complete {
		return PowerProposal{Method: PowerUnknown}, nil
	}
	if blackout {
		return PowerProposal{Method: PowerWaitBlackout}, nil
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
		for _, b := range v.Buildings {
			if !same(b) {
				continue
			}
			w, _ := b.BaseW.Value()
			actual, _ := b.OutputW.Value()
			output += actual
			if w < 0 {
				demand -= w
			}
			if w > 0 {
				connectedProducers++
				capacity += w
				f, _ := b.Forbidden.Value()
				s, _ := b.SwitchedOn.Value()
				disabledProducer = disabledProducer || f || !s
				empty, _ := b.OutOfFuel.Value()
				broken, _ := b.BrokenDown.Value()
				unfueledProducer = unfueledProducer || empty
				brokenProducer = brokenProducer || broken
			}
		}
		// Reserve runway only matters while the network is powered by stored
		// energy: a draining battery keeps consumers on until it does not.
		reserveShort := false
		if connected && powered {
			if net, ok := networks[network]; ok {
				if days, known := net.ReserveDays().Value(); known && days < planning.ReserveMinDays {
					reserveShort = true
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
			case reserveShort && output < 0:
				// Installed capacity covers demand on paper but the network is
				// still draining its reserve (night-time solar, part-time
				// generation): add generation sized to the observed load.
				return generate(p, target, producers, planning)
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
		return generate(p, target, producers, planning)
	}
	return result, nil
}

// generate proposes one more generator for target's network. Native producer
// identity, rather than fluctuating output, distinguishes an additional
// capacity proposal from replay of the same deficit.
func generate(p PowerProposal, target PowerSite, producers []PowerSite, planning PowerPlanning) (PowerProposal, error) {
	definition := SelectGenerator(planning.Generators)
	if definition == "" {
		p.Method = PowerNoGenerator
		return p, nil
	}
	ids := make([]string, 0, len(producers))
	for _, b := range producers {
		ids = append(ids, b.ID)
	}
	sort.Strings(ids)
	p.Method, p.Definition = PowerGenerate, definition
	p.Key = powerMethodKey("generate", target.ID+"/"+strings.Join(ids, "/"))
	return p, nil
}

func powerMethodKey(prefix, identity string) domain.MethodID {
	digest := sha256.Sum256([]byte(identity))
	return domain.MethodID(fmt.Sprintf("%s-%x", prefix, digest[:12]))
}

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
				if len(previous) >= 2048 {
					return nil
				}
				previous[next] = c
				queue = append(queue, next)
			}
		}
	}
	return nil
}
