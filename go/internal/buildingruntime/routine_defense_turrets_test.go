package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func turretReading() observation.RoutineReading {
	var read observation.RoutineReading
	read.Projection.Definitions = []observation.PlanningDefinition{
		{Name: defenseTurretDefinition, Available: domain.Known(true), Stuff: domain.Known("Steel"), Research: []string{"GunTurrets"}, PowerW: domain.Known(80.0), Costs: domain.Known([]policy.Amount{{Resource: "Steel", Count: 70}, {Resource: "ComponentIndustrial", Count: 3}})},
		{Name: defenseConduitDefinition, Available: domain.Known(true), Costs: domain.Known([]policy.Amount{{Resource: "Steel", Count: 1}})},
		{Name: "Wall", Available: domain.Known(true)},
	}
	read.Projection.Facts.Research = domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"Electricity", "GunTurrets"}})
	read.Projection.Resources = domain.Known(map[policy.Resource]int64{"Steel": 300, "ComponentIndustrial": 10})
	generator := policy.PowerSite{ID: "gen", Definition: "WoodFiredGenerator", Cell: domain.Cell{X: 20, Z: 20}, Occupied: []domain.Cell{{X: 20, Z: 20}, {X: 21, Z: 20}, {X: 20, Z: 21}, {X: 21, Z: 21}}}
	generator.Network = domain.Known("net-1")
	generator.BaseW = domain.Known(1000.0)
	other := policy.PowerSite{ID: "gen2", Definition: "WoodFiredGenerator", Cell: domain.Cell{X: 60, Z: 60}, Occupied: []domain.Cell{{X: 60, Z: 60}}}
	other.Network = domain.Known("net-2")
	turret := policy.PowerSite{ID: "turret", Definition: defenseTurretDefinition, Cell: domain.Cell{X: 30, Z: 20}, Occupied: []domain.Cell{{X: 30, Z: 20}}}
	turret.Network, turret.Powered = domain.Known("net-1"), domain.Known(false)
	read.Projection.PowerPlanning = domain.Known(policy.PowerTopology{
		Buildings: []policy.PowerSite{generator, other, turret},
		// A chain from beside the generator, a stray conduit nine cells
		// out that touches nothing, and the other network's conduit.
		Conduits: []domain.Cell{{X: 22, Z: 20}, {X: 23, Z: 20}, {X: 24, Z: 20}, {X: 24, Z: 21}, {X: 30, Z: 30}, {X: 61, Z: 60}},
		Networks: []policy.PowerNetworkFact{
			{ID: "net-1", GenerationW: domain.Known(1000.0), ConsumptionW: domain.Known(700.0)},
			{ID: "net-2", GenerationW: domain.Known(1000.0), ConsumptionW: domain.Known(100.0)},
		},
	})
	return read
}

func TestDefenseTurretRequestObservesEveryGate(t *testing.T) {
	t.Parallel()
	request := defenseTurretRequest(turretReading())
	q := request.Turret
	if q.Definition != defenseTurretDefinition || q.Conduit != defenseConduitDefinition || q.Max != policy.TurretBudget(domain.Unknown[float64]()) {
		t.Fatalf("%+v", q)
	}
	if v, k := q.Available.Value(); !k || !v {
		t.Fatal("researched turret unavailable", q.Available)
	}
	if v, k := q.DrawW.Value(); !k || v != 80 {
		t.Fatal(q.DrawW)
	}
	// The mini turret is made from metallic stuff: the observed default
	// material is what the tier builds with.
	if q.Stuff != "Steel" {
		t.Fatal("turret stuff", q.Stuff)
	}
	// The network with the most spare watts wins, and only conduits that
	// chain from within connector reach of its buildings carry it.
	if v, k := q.SpareW.Value(); !k || v != 900 {
		t.Fatal(q.SpareW)
	}
	if !reflect.DeepEqual(q.Transmitters, []domain.Cell{{X: 61, Z: 60}}) {
		t.Fatal(q.Transmitters)
	}
	if !reflect.DeepEqual(request.UnitCosts[defenseTurretDefinition], []policy.Amount{{Resource: "Steel", Count: 70}, {Resource: "ComponentIndustrial", Count: 3}}) || !reflect.DeepEqual(request.UnitCosts[defenseConduitDefinition], []policy.Amount{{Resource: "Steel", Count: 1}}) {
		t.Fatal(request.UnitCosts)
	}
	if !request.TurretGatesOpen() {
		t.Fatal("gates closed")
	}
	// Without the other network, net-1's chain is the transmitter set and
	// the stray conduit stays out.
	read := turretReading()
	topology, _ := read.Projection.PowerPlanning.Value()
	topology.Networks = topology.Networks[:1]
	read.Projection.PowerPlanning = domain.Known(topology)
	if got := defenseTurretRequest(read).Turret.Transmitters; !reflect.DeepEqual(got, []domain.Cell{{X: 22, Z: 20}, {X: 23, Z: 20}, {X: 24, Z: 20}, {X: 24, Z: 21}}) {
		t.Fatal(got)
	}
	// Research is checked against the snapshot, not the definition alone:
	// an unfinished prerequisite closes the gate, an unknown snapshot
	// leaves it unknown, and a definition without prerequisites needs none.
	read = turretReading()
	read.Projection.Facts.Research = domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"Electricity"}})
	if v, k := defenseTurretRequest(read).Turret.Available.Value(); !k || v {
		t.Fatal("unfinished research left the turret available")
	}
	read.Projection.Facts.Research = domain.Unknown[policy.ResearchFacts]()
	if _, k := defenseTurretRequest(read).Turret.Available.Value(); k {
		t.Fatal("unknown research snapshot decided availability")
	}
	read.Projection.Definitions[0].Research = nil
	if v, k := defenseTurretRequest(read).Turret.Available.Value(); !k || !v {
		t.Fatal("definition without prerequisites")
	}
	read = turretReading()
	read.Projection.Definitions[0].Available = domain.Known(false)
	if v, k := defenseTurretRequest(read).Turret.Available.Value(); !k || v {
		t.Fatal("native unavailability ignored")
	}
	// No power census, no network, or no generating network: no spare watts.
	read = turretReading()
	read.Projection.PowerPlanning = domain.Unknown[policy.PowerTopology]()
	if q := defenseTurretRequest(read).Turret; q.SpareW != domain.Unknown[float64]() || q.Transmitters != nil {
		t.Fatalf("%+v", q)
	}
	read = turretReading()
	topology, _ = read.Projection.PowerPlanning.Value()
	topology.Networks = nil
	read.Projection.PowerPlanning = domain.Known(topology)
	if defenseTurretRequest(read).TurretGatesOpen() {
		t.Fatal("gate open without a network")
	}
	read = turretReading()
	read.Projection.Definitions = read.Projection.Definitions[2:]
	if q := defenseTurretRequest(read); q.TurretGatesOpen() || q.Turret.DrawW != domain.Unknown[float64]() {
		t.Fatal("gate open without the planning definition")
	}
}

func TestDefenseCensusStandsConduitsAndPower(t *testing.T) {
	t.Parallel()
	turret, conduit, wall := domain.Cell{X: 30, Z: 20}, domain.Cell{X: 31, Z: 20}, domain.Cell{X: 1, Z: 1}
	record := store.DefenseLayoutRecord{Complete: true, Tiers: []store.DefenseTierRecord{
		{Name: policy.TierFunnel, Built: true, Buildings: []store.DefenseBuilding{{Definition: "Wall", Cell: wall}}},
		{Name: policy.TierTurrets, Built: true, Attempts: 1, Buildings: []store.DefenseBuilding{{Definition: defenseTurretDefinition, Cell: turret, Rotation: domain.North}, {Definition: defenseConduitDefinition, Cell: conduit, Rotation: domain.North}}},
	}}
	// Conduits are not edifices: the conduit census stands them.
	site := policy.PowerSite{ID: "Turret_MiniTurret1", Definition: defenseTurretDefinition, Cell: turret, PowerBuilding: policy.PowerBuilding{Powered: domain.Known(true), OutOfFuel: domain.Known(false), Fuel: domain.Known(60.0), TargetFuel: domain.Known(60.0), FuelDefinitions: []string{"Steel"}}}
	census := &defenseCensus{edifice: map[domain.Cell]string{wall: "Wall", turret: defenseTurretDefinition}, conduits: map[domain.Cell]bool{conduit: true}, consumers: map[domain.Cell]policy.PowerSite{turret: site}}
	if defenseTierCensus(&record, census) || !record.Tiers[1].Built {
		t.Fatal("standing tier changed")
	}
	hauler := policy.WorkPawn{ID: "h", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]policy.WorkPriority{{Work: policy.WorkHauling, Priority: 3}})}
	stock := domain.Known(map[policy.Resource]int64{"Steel": 100})
	upkeep := func() policy.DefenseTurretUpkeep {
		return policy.DefenseRearmTurrets(defenseTurretFacts(record, census), []policy.WorkPawn{hauler}, stock)
	}
	if u := upkeep(); len(u.Unpowered) != 0 || len(u.Empty) != 0 || len(u.Rearm) != 0 {
		t.Fatalf("%+v", u)
	}
	// A lost conduit re-opens the tier by that conduit alone.
	census.conduits = map[domain.Cell]bool{}
	if !defenseTierCensus(&record, census) || record.Tiers[1].Built || record.Tiers[1].Attempts != 0 {
		t.Fatalf("%+v", record.Tiers[1])
	}
	_, buildings, _ := record.Tier(policy.TierTurrets)
	if missing := defenseMissingBuildings(buildings, census); len(missing) != 1 || missing[0].Cell() != conduit {
		t.Fatal(missing)
	}
	// A standing turret without power is a deficit, not a missing
	// building; an unknown power state is neither.
	census.conduits[conduit] = true
	site.Powered = domain.Known(false)
	census.consumers[turret] = site
	defenseTierCensus(&record, census)
	if !record.Tiers[1].Built {
		t.Fatal("unpowered turret counted as lost")
	}
	if u := upkeep(); !reflect.DeepEqual(u.Unpowered, []domain.Cell{turret}) || len(u.Empty) != 0 {
		t.Fatalf("%+v", u)
	}
	// An empty barrel is a deficit with a rearm order on the turret's
	// census identity (#205); the facts carry the census fuel definitions.
	site.Powered, site.OutOfFuel, site.Fuel = domain.Known(true), domain.Known(true), domain.Known(0.0)
	census.consumers[turret] = site
	facts := defenseTurretFacts(record, census)
	if len(facts) != 1 || facts[0].ID != site.ID || !reflect.DeepEqual(facts[0].FuelDefinitions, []policy.Resource{"Steel"}) {
		t.Fatalf("%+v", facts)
	}
	if u := upkeep(); !reflect.DeepEqual(u.Empty, []domain.Cell{turret}) || !reflect.DeepEqual(u.Rearm, []policy.DefenseRearm{{Turret: site.ID, Cell: turret, Pawn: "h", Fuel: "Steel"}}) {
		t.Fatalf("%+v", u)
	}
	// A turret cell the power census does not carry has unknown facts and
	// no deficit; so does a layout without any census.
	census.consumers = map[domain.Cell]policy.PowerSite{}
	if u := upkeep(); len(u.Unpowered) != 0 || len(u.Empty) != 0 {
		t.Fatal("unknown power state became a deficit")
	}
	if facts := defenseTurretFacts(record, nil); facts != nil {
		t.Fatal(facts)
	}
}

func TestDefenseRecordGeometryAndTurretTier(t *testing.T) {
	t.Parallel()
	record := store.DefenseLayoutRecord{World: store.World{Colony: "c", Load: "l"}, Goal: "g", Entry: domain.Cell{X: 9, Z: 14}, Toward: domain.North, Firing: []domain.Cell{{X: 9, Z: 23}},
		TrapLane: []domain.Cell{{X: 9, Z: 14}}, SafeLane: []domain.Cell{{X: 8, Z: 14}}, Tiers: []store.DefenseTierRecord{
			{Name: policy.TierFiringLine, Reserved: []domain.Cell{{X: 9, Z: 22}}, Buildings: []store.DefenseBuilding{{Definition: "Barricade", Cell: domain.Cell{X: 9, Z: 22}}}},
			{Name: policy.TierTurrets, Reserved: []domain.Cell{{X: 5, Z: 23}}, Buildings: []store.DefenseBuilding{{Definition: defenseTurretDefinition, Cell: domain.Cell{X: 5, Z: 23}}}},
		}}
	g := defenseRecordGeometry(record)
	want := policy.DefenseGeometry{Entry: record.Entry, Approach: []domain.Cell{{X: 9, Z: 14}}, Toward: domain.North, Firing: []domain.Cell{{X: 9, Z: 23}}, Lanes: []domain.Cell{{X: 9, Z: 14}, {X: 8, Z: 14}}, Reserved: []domain.Cell{{X: 9, Z: 22}, {X: 9, Z: 22}}}
	if !reflect.DeepEqual(g, want) {
		t.Fatalf("%+v", g)
	}
	turret, _ := domain.NewBuilding(defenseTurretDefinition, domain.Cell{X: 13, Z: 23}, domain.North, "")
	record.Tiers[1].Built, record.Tiers[1].Attempts = true, 2
	record.SetTurretTier(policy.DefenseTier{Name: policy.TierTurrets, Buildings: []domain.Building{turret}, Reserved: []domain.Cell{{X: 13, Z: 23}}})
	if tier := record.Tiers[1]; tier.Built || tier.Attempts != 0 || len(tier.Buildings) != 1 || tier.Buildings[0].Cell != (domain.Cell{X: 13, Z: 23}) || len(record.Tiers) != 2 {
		t.Fatalf("%+v", record.Tiers)
	}
	record.Tiers = record.Tiers[:1]
	record.SetTurretTier(policy.DefenseTier{Name: policy.TierTurrets, Buildings: []domain.Building{turret}})
	if len(record.Tiers) != 2 || record.Tiers[1].Name != policy.TierTurrets {
		t.Fatal(record.Tiers)
	}
	record.TurretsProbedTick = -1
	if record.Validate() == nil {
		t.Fatal("negative probe tick accepted")
	}
	// A placed tier is never re-proposed; an empty or absent one is, once
	// per reverify interval.
	record.TurretsProbedTick = 0
	if defenseTurretsDue(record, 100) {
		t.Fatal("placed tier due")
	}
	record.Tiers[1].Buildings = nil
	if !defenseTurretsDue(record, 100) {
		t.Fatal("empty tier never probed")
	}
	record.TurretsProbedTick = 100
	if defenseTurretsDue(record, 100+defenseReverifyTicks-1) || !defenseTurretsDue(record, 100+defenseReverifyTicks) {
		t.Fatal("probe interval")
	}
	record.Tiers = record.Tiers[:1]
	if !defenseTurretsDue(record, 100+defenseReverifyTicks) {
		t.Fatal("absent tier not due")
	}
}

func TestDefenseRearmAttemptsCountTheTurretWithinTheWindow(t *testing.T) {
	t.Parallel()
	history := []domain.GoalMethod{
		{Method: domain.MethodID(defenseRearmPrefix("T1") + "1000")},
		{Method: domain.MethodID(defenseRearmPrefix("T1") + "2000")},
		{Method: domain.MethodID(defenseRearmPrefix("T2") + "2000")},
		{Method: domain.MethodID(defenseRearmPrefix("T1") + "x")},
		{Method: "defense-turrets-0"},
	}
	if got := defenseRearmAttempts(history, "T1", 3000); got != 2 {
		t.Fatal(got)
	}
	// An order older than the window no longer counts; another turret's
	// never does.
	if got := defenseRearmAttempts(history, "T1", 1000+defenseRearmWindowTicks); got != 1 {
		t.Fatal(got)
	}
	if got := defenseRearmAttempts(history, "T3", 3000); got != 0 {
		t.Fatal(got)
	}
}
