package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// How many power sites a first plan owes (#2999). The count is not a number the
// planner holds; it is what the planned demand needs, found by adding sites one
// at a time until the budget has no generation shortfall:
//
//	demand      the draw of every station the groups hold (Station.BaseW), and
//	            the lamps their floor needs (a standing lamp lights the circle of
//	            its glow reach, so a group of Area cells owes Area over that
//	            circle's cells, rounded up)
//	supply      what each placed site delivers over a day, from the catalog's
//	            rows: a turbine at its own lane's output (the cells blocked there,
//	            not the peak), a solar plot by the day curve
//	storage     the planned battery room's slots at the catalog battery's
//	            capacity, which carries the solar night
//	shortfall   ComputePowerBudget's GenerationShortfallW: the constant
//	            generation still to add
//
// Each step quotes the next turbine and the next solar plot at their cheapest
// sites (siteRun.quote) and takes the one whose price (build, conduit, wall and
// ground, in watts) is least per watt it delivers, so turbines come first while
// the wind is good and solar fills in where lanes or wind are thin. A catalog
// without the rows, or a colony without the steel price, makes the count
// unknown (ErrStationFacts), never a default. Replans keep the sites the plan
// holds and add none: the colony's later demand grows through SelectPowerMethod,
// which builds on the planned sites.

// PowerConduitDefinition is the conduit a site is wired to the grid with.
const PowerConduitDefinition = siteConduit

// siteConduit is the DefTables table (def_tables.go) that owns the name.
const siteConduit = "PowerConduit"

// SupplyRow is one generator definition as the site count sees it: nominal
// watts, the market value of its cheapest cost list and how it delivers over a
// day (DefinitionCatalog.PowerSources). The zero value is unknown.
type SupplyRow struct {
	NominalW, Build float64
	Profile         PowerSourceProfile
}

func (r SupplyRow) known() bool {
	return finitePositive(r.NominalW) && finitePositive(r.Build) && finitePositive(r.Profile.Daily)
}

// PowerModel is what the power sites are derived and priced from, read from the
// defs (bridge): the turbine and the solar generator, the battery, and the
// market value of the conduit's and the ring wall's cheapest cost lists, per
// cell. The zero value is unknown.
type PowerModel struct {
	Wind, Solar   SupplyRow
	Battery       PowerBattery
	Conduit, Wall float64
}

func (m PowerModel) known() bool {
	return m.Wind.known() && m.Solar.known() && finitePositive(m.Conduit) && finitePositive(m.Wall) &&
		finitePositive(m.Battery.CapacityWD) && finitePositive(m.Battery.Efficiency)
}

// batteryRoomCapacityWD is the storage of the planned battery room: its slots at
// the catalog battery's capacity.
func (m PowerModel) batteryRoomCapacityWD() float64 {
	room := PlannedRoom{Role: PlannedBattery, Region: RectRegion(Rectangle{Width: roomSizeTable.Battery[0], Height: roomSizeTable.Battery[1]}), DoorRot: domain.South}
	return float64(len(BatterySlots(room))) * m.Battery.CapacityWD
}

// plannedDemandW is the watts the groups draw: their powered stations and the
// lamps that light their floor. A group that wants a lamp the catalog does not
// price makes the demand unknown.
func (f StationFacts) plannedDemandW(groups []RoomGroup) (float64, error) {
	lamp, reach := f.Catalog.Light.Standing, f.Catalog.Light.StandingReach()
	demand := 0.0
	for _, g := range groups {
		for _, s := range g.Stations {
			if st, ok := f.Catalog.Station(s.Def); ok && st.DrawsPower() {
				demand += -st.BaseW * float64(s.Count)
			}
		}
		if g.Area <= 0 || slices.Contains(g.Requires, EnvOpenSky) {
			continue
		}
		if !finitePositive(lamp.PowerW) || !finitePositive(reach) {
			return 0, unknownStationFact("lamp")
		}
		demand += lamp.PowerW * math.Ceil(float64(g.Area)/(math.Pi*reach*reach))
	}
	return demand, nil
}

// sitePrice is the watts a free site is priced in, from the catalog's power rows
// and the steel price; false while either is unknown.
func (f StationFacts) sitePrice() (SitePrice, bool) {
	steel, err := f.Items.MarketValue(siteSteelUnit)
	if err != nil || !finitePositive(steel) || !f.Catalog.Power.known() {
		return SitePrice{}, false
	}
	w := DefaultSiteTypeWeights()
	p := f.Catalog.Power
	return SitePrice{Weights: w, WindW: p.Wind.NominalW * p.Wind.Profile.Daily, ConduitW: BuildW(w, p.Conduit, steel), WallW: BuildW(w, p.Wall, steel)}, true
}

// powerSites places the power sites around the embedding: the plan's own, and
// for a first plan (Derive) the turbines and solar plots its planned demand
// needs. It returns the nodes in the order they were taken.
func (f StationFacts) powerSites(groups []RoomGroup, embedIn EmbedInput, embedding Embedding) ([]SiteNode, SiteEmbedding, error) {
	nodes := PowerSiteNodes(f.Plan)
	price, priced := f.sitePrice() // a replan's advice places the sites it holds, priced or not
	in := SiteEmbedInput{Embed: embedIn, Embedding: embedding, Nodes: nodes, Price: price}
	if !f.Derive {
		if len(nodes) == 0 {
			return nil, SiteEmbedding{}, nil
		}
		out, err := EmbedSites(in)
		return nodes, out, err
	}
	demand, err := f.plannedDemandW(groups)
	if err != nil {
		return nil, SiteEmbedding{}, err
	}
	if demand > 0 && !priced {
		if steel, err := f.Items.MarketValue(siteSteelUnit); err != nil || !finitePositive(steel) {
			return nil, SiteEmbedding{}, unknownStationFact("steel value")
		}
		return nil, SiteEmbedding{}, unknownStationFact("power supply")
	}
	run, err := newSiteRun(in)
	if err != nil {
		return nil, SiteEmbedding{}, err
	}
	for _, n := range nodes {
		if err := run.admit(n); err != nil {
			return nil, SiteEmbedding{}, err
		}
	}
	for _, n := range nodes {
		if n.Anchor != nil {
			run.place(n)
		}
	}
	for _, n := range nodes {
		if n.Anchor == nil {
			run.place(n)
		}
	}
	if demand > 0 {
		if nodes, err = f.growSites(run, nodes, demand); err != nil {
			return nil, SiteEmbedding{}, err
		}
	}
	return nodes, run.result(), nil
}

// growSites adds sites to the run, one at a time, until the budget has no
// generation shortfall or no ground holds another.
func (f StationFacts) growSites(run *siteRun, nodes []SiteNode, demand float64) ([]SiteNode, error) {
	p := f.Catalog.Power
	steel, _ := f.Items.MarketValue(siteSteelUnit)
	w := DefaultSiteTypeWeights()
	windBuild, solarBuild := BuildW(w, p.Wind.Build, steel), BuildW(w, p.Solar.Build, steel)
	solarW := p.Solar.NominalW * p.Solar.Profile.Daily
	turbines, plots := 0, 0
	for {
		if f.siteBudget(run, nodes, demand).GenerationShortfallW <= 0 {
			return nodes, nil
		}
		turbine, plot := turbineNode(windNodeID(turbines+1)), solarNode(solarNodeID(plots+1))
		tq, tok := run.quote(turbine)
		sq, sok := run.quote(plot)
		// Price per delivered watt: what the site costs to build and reach,
		// over what it delivers once its lane has taken its share.
		tRatio, sRatio := math.Inf(1), math.Inf(1)
		if tok && p.Wind.NominalW*p.Wind.Profile.Daily-tq.LaneW > 0 {
			tRatio = (tq.CostW + windBuild) / (p.Wind.NominalW*p.Wind.Profile.Daily - tq.LaneW)
		}
		if sok && solarW > 0 {
			sRatio = (sq.CostW + solarBuild) / solarW
		}
		switch {
		case math.IsInf(tRatio, 1) && math.IsInf(sRatio, 1):
			run.place(turbine) // no ground, or none that delivers: the decision says so
			return nodes, nil
		case tRatio <= sRatio:
			run.take(turbine, tq)
			nodes = append(nodes, turbine)
			turbines++
		default:
			run.take(plot, sq)
			nodes = append(nodes, plot)
			plots++
		}
	}
}

// siteBudget is the power budget of the demand against the sites placed so far:
// each turbine at the output its lane leaves, each solar plot by the day curve,
// and the planned battery room as the bank.
func (f StationFacts) siteBudget(run *siteRun, nodes []SiteNode, demand float64) PowerBudget {
	p := f.Catalog.Power
	placed := run.result()
	in := PowerBudgetInput{DemandW: demand, CapacityWD: p.batteryRoomCapacityWD(), ChargeEfficiency: p.Battery.Efficiency, StorageMargin: DefaultPowerPlanning().StorageMargin}
	for _, n := range nodes {
		site, ok := placed.Site(n.ID)
		if !ok {
			continue
		}
		switch n.Kind {
		case NodeTurbine:
			blocked := 0
			for _, l := range site.Lanes {
				if l.Wind {
					blocked += l.Blocked
				}
			}
			out := WindOutput(blocked)
			in.Producers = append(in.Producers, PowerProducer{Definition: WindTurbineDefinition, NominalW: p.Wind.NominalW, Profile: PowerSourceProfile{Daily: p.Wind.Profile.Daily * out, Night: p.Wind.Profile.Night * out}})
		case NodeSolar:
			in.Producers = append(in.Producers, PowerProducer{Definition: SolarDefinition, NominalW: p.Solar.NominalW, Profile: p.Solar.Profile})
		}
	}
	return ComputePowerBudget(in)
}
