package buildingruntime

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"sort"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/supplysim"
)

// The resource matrix (epic #2140, issue #2151) runs wood, stone, steel,
// components and plasteel over source mixes and shocks through
// resPlanner and asserts direction and ordering. A scenario check that fails
// today is recorded in testdata/resource-matrix-baseline.json; the baseline
// only shrinks: a new failure fails the test, and a baselined check that now
// passes fails it until the entry is removed (-update-resource-baseline
// rewrites the file but never adds an entry).

var updateResourceBaseline = flag.Bool("update-resource-baseline", false, "drop fixed entries from testdata/resource-matrix-baseline.json")

const resourceBaselinePath = "testdata/resource-matrix-baseline.json"

const (
	chkRestore = "restore" // the floor is restored within restoreDays of the last shock
	chkEdge    = "edge"    // a shortfall edge raises exactly the missing resource
	chkSingle  = "single"  // one planner dispatches a resource per day
	chkTTL     = "ttl"     // no planner yields to a bid older than its TTL
	chkHold    = "hold"    // no source: a hold, no dispatch
	chkGate    = "gate"    // a deep drill only on a runway deficit, with research and power
	chkDrill   = "drill"   // a deep drill's bid is positive when it is the only source
)

const restoreDays = 25

type resScenario struct {
	name      string
	world     supplysim.World
	spec      map[string]resSpec
	targets   map[policy.Resource]int64
	research  bool
	good      supplysim.Good
	restoreTo float64
	days      int
	checks    []string
	// edgeDay is the day a Requisition opens its shortfall edge.
	edgeDay int
	// lostDay is the day the last source is removed; 0 when one remains.
	lostDay int
}

type resFixture struct {
	good     supplysim.Good
	stock    float64
	perDay   float64
	restore  float64
	research bool
}

func resTrees() ([]supplysim.Source, map[string]resSpec) {
	return []supplysim.Source{supplysim.NewTrees("chop", 3000, 4000, 80)}, map[string]resSpec{"chop": {resChop, 40}}
}

func resWorld(f resFixture, srcs []supplysim.Source) supplysim.World {
	w := supplysim.World{Seed: 7, Workers: 6, Power: 400, Sources: srcs,
		Stock: map[supplysim.Good]float64{supplysim.Steel: 500, supplysimComponents: 40, supplysim.Silver: 5000, f.good: f.stock},
		Floors: []supplysim.Floor{
			{Good: supplysim.Wood, Min: supplysim.WoodMin, Target: supplysim.WoodTarget, Max: supplysim.WoodMax},
			{Good: supplysim.Steel, Min: supplysim.SteelFloor, Target: supplysim.SteelFloor, Max: 1000},
			{Good: supplysimComponents, Min: supplysim.ComponentFloor, Target: supplysim.ComponentFloor, Max: 100},
		},
		Consumers: []supplysim.Consumer{{Name: "use", Good: f.good, PerDay: f.perDay}}}
	if f.good == supplysim.StoneBlocks {
		w.Stock[supplysim.StoneChunks] = 20
	}
	return w
}

const supplysimComponents = supplysim.Components

func resKeep(m map[string]resSpec, add map[string]resSpec) map[string]resSpec {
	for k, v := range add {
		m[k] = v
	}
	return m
}

func resTrader(good supplysim.Good, restock, perUnit, price float64) supplysim.Source {
	return supplysim.NewTradeOf("trader", good, supplysim.Window{Period: 10, From: 3, To: 5}, restock, perUnit, price)
}

// resMixes is every source mix per good. Each returns fresh sources.
var resMixes = map[supplysim.Good]map[string]func() ([]supplysim.Source, map[string]resSpec){
	supplysim.Wood: {
		"chop": resTrees,
		"chop+trade": func() ([]supplysim.Source, map[string]resSpec) {
			s, m := resTrees()
			return append(s, resTrader(supplysim.Wood, 400, 1, 1.2)), resKeep(m, map[string]resSpec{"trader": {Role: resTrade}})
		},
		"trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{resTrader(supplysim.Wood, 400, 1, 1.2)}, map[string]resSpec{"trader": {Role: resTrade}}
		},
	},
	supplysim.StoneBlocks: {
		"produce": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewStonecutter("cutter", 4)}, map[string]resSpec{"cutter": {Role: resProduce}}
		},
		"mine+produce": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewQuarry("quarry", 300, 10), supplysim.NewStonecutter("cutter", 4)},
				map[string]resSpec{"quarry": {resMine, 20}, "cutter": {Role: resProduce}}
		},
		"trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{resTrader(supplysim.StoneBlocks, 300, 1, 0.6)}, map[string]resSpec{"trader": {Role: resTrade}}
		},
	},
	supplysim.Steel: {
		"mine": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewVein("mine", supplysim.Steel, 800, 40, 0)}, map[string]resSpec{"mine": {resMine, 30}}
		},
		"mine+trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewVein("mine", supplysim.Steel, 800, 40, 0), resTrader(supplysim.Steel, 300, 1, 8)},
				map[string]resSpec{"mine": {resMine, 30}, "trader": {Role: resTrade}}
		},
		"drill": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewDeepDrill("drill", supplysim.Steel, 800, 25, 200)}, map[string]resSpec{"drill": {resDrill, 40}}
		},
		"mine+drill": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewVein("mine", supplysim.Steel, 150, 40, 0), supplysim.NewDeepDrill("drill", supplysim.Steel, 800, 25, 200)},
				map[string]resSpec{"mine": {resMine, 30}, "drill": {resDrill, 40}}
		},
		"trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{resTrader(supplysim.Steel, 300, 1, 8)}, map[string]resSpec{"trader": {Role: resTrade}}
		},
		"loot": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewLoot("loot", supplysim.Steel, 600, 60)}, map[string]resSpec{"loot": {resLoot, 60}}
		},
		"salvage": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewSalvage("wreck", supplysim.Steel, 600, 60)}, map[string]resSpec{"wreck": {resSalvage, 60}}
		},
	},
	supplysim.Components: {
		"produce": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewRecipe("bench", supplysim.Steel, 12, supplysim.Components, 1, 3)}, map[string]resSpec{"bench": {Role: resProduce}}
		},
		"trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{resTrader(supplysim.Components, 30, 1, 60)}, map[string]resSpec{"trader": {Role: resTrade}}
		},
		"produce+trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewRecipe("bench", supplysim.Steel, 12, supplysim.Components, 1, 3), resTrader(supplysim.Components, 30, 1, 60)},
				map[string]resSpec{"bench": {Role: resProduce}, "trader": {Role: resTrade}}
		},
		"salvage": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewSalvage("wreck", supplysim.Components, 40, 4)}, map[string]resSpec{"wreck": {resSalvage, 60}}
		},
	},
	supplysim.Plasteel: {
		"drill": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewDeepDrill("drill", supplysim.Plasteel, 400, 10, 200)}, map[string]resSpec{"drill": {resDrill, 40}}
		},
		"trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{resTrader(supplysim.Plasteel, 100, 1, 25)}, map[string]resSpec{"trader": {Role: resTrade}}
		},
		"drill+trade": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewDeepDrill("drill", supplysim.Plasteel, 400, 10, 200), resTrader(supplysim.Plasteel, 100, 1, 25)},
				map[string]resSpec{"drill": {resDrill, 40}, "trader": {Role: resTrade}}
		},
		"loot": func() ([]supplysim.Source, map[string]resSpec) {
			return []supplysim.Source{supplysim.NewLoot("loot", supplysim.Plasteel, 150, 10)}, map[string]resSpec{"loot": {resLoot, 60}}
		},
	},
}

var resFixtures = map[supplysim.Good]resFixture{
	supplysim.Wood:        {good: supplysim.Wood, stock: 300, perDay: 25, restore: supplysim.WoodTarget},
	supplysim.StoneBlocks: {good: supplysim.StoneBlocks, stock: 400, perDay: 30, restore: float64(policy.DefaultStoneBlockTarget)},
	supplysim.Steel:       {good: supplysim.Steel, stock: 300, perDay: 15, restore: supplysim.SteelFloor, research: true},
	supplysim.Components:  {good: supplysim.Components, stock: 30, perDay: 1, restore: supplysim.ComponentFloor},
	// Plasteel has no configured floor: only the runway forecast asks for it.
	supplysim.Plasteel: {good: supplysim.Plasteel, stock: 40, perDay: 4, restore: 20, research: true},
}

// resShocks maps a shock-set name to its shocks for a scenario; ok is false
// when the shock does not apply to the mix.
func resShocks(set string, g supplysim.Good, mix string, srcs []supplysim.Source) (shocks []supplysim.Shock, lost, edge int, ok bool) {
	primary := srcs[0].ID
	has := func(prefix string) string {
		for _, s := range srcs {
			if strings.HasPrefix(s.ID, prefix) {
				return s.ID
			}
		}
		return ""
	}
	switch set {
	case "none":
		return nil, 0, 0, true
	case "surge":
		return []supplysim.Shock{{Day: 10, Kind: supplysim.SurgeDemand, Good: g, Factor: 4, Days: 8}}, 0, 0, true
	case "destroy":
		return []supplysim.Shock{{Day: 15, Kind: supplysim.DestroyStock, Good: g, Factor: 0.9}}, 0, 0, true
	case "capacity":
		if primary == "trader" {
			return nil, 0, 0, false
		}
		return []supplysim.Shock{{Day: 10, Kind: supplysim.ScaleCapacity, Source: primary, Factor: 0.5}}, 0, 0, true
	case "remove":
		sh := []supplysim.Shock{{Day: 12, Kind: supplysim.RemoveSource, Source: primary}}
		if len(srcs) == 1 || g == supplysim.StoneBlocks && mix != "mine+produce" {
			return sh, 12, 0, true
		}
		return sh, 0, 0, true
	case "pause":
		if has("chop") == "" {
			return nil, 0, 0, false
		}
		return []supplysim.Shock{{Day: 8, Kind: supplysim.PauseGrowth, Source: "chop", Days: 20}, {Day: 8, Kind: supplysim.ScaleCapacity, Source: "chop", Factor: 0.5}}, 0, 0, true
	case "threat":
		if has("loot") == "" && has("wreck") == "" {
			return nil, 0, 0, false
		}
		return []supplysim.Shock{{Day: 5, Kind: supplysim.Threat, Days: 12}}, 0, 0, true
	case "stopwork":
		if has("trader") == "" {
			return nil, 0, 0, false
		}
		return []supplysim.Shock{{Day: 8, Kind: supplysim.StopWork, Source: "trader", Days: 15}}, 0, 0, true
	case "power":
		if has("drill") == "" {
			return nil, 0, 0, false
		}
		return []supplysim.Shock{{Day: 10, Kind: supplysim.PowerLoss, Factor: 0, Days: 12}}, 0, 0, true
	case "storage":
		return []supplysim.Shock{{Day: 8, Kind: supplysim.StorageFull, Good: g, Factor: 60, Days: 8}}, 0, 0, true
	case "requisition":
		return []supplysim.Shock{{Day: 12, Kind: supplysim.Requisition, Good: g, Factor: resRequisition[g]}}, 0, 12, true
	}
	return nil, 0, 0, false
}

// resRequisition is a build cost well above the fixture's stock, so the
// shortfall edge is real.
var resRequisition = map[supplysim.Good]float64{supplysim.Wood: 900, supplysim.StoneBlocks: 800, supplysim.Steel: 900, supplysim.Components: 80, supplysim.Plasteel: 200}

var resShockSets = []string{"none", "surge", "destroy", "capacity", "remove", "pause", "threat", "stopwork", "power", "storage", "requisition"}

func resScenarios() []resScenario {
	var out []resScenario
	goods := []supplysim.Good{supplysim.Wood, supplysim.StoneBlocks, supplysim.Steel, supplysim.Components, supplysim.Plasteel}
	for _, g := range goods {
		f := resFixtures[g]
		mixes := make([]string, 0, len(resMixes[g]))
		for m := range resMixes[g] {
			mixes = append(mixes, m)
		}
		sort.Strings(mixes)
		for _, mix := range mixes {
			for _, set := range resShockSets {
				srcs, spec := resMixes[g][mix]()
				shocks, lost, edge, ok := resShocks(set, g, mix, srcs)
				if !ok {
					continue
				}
				f.good = g
				w := resWorld(f, srcs)
				w.Shocks = shocks
				sc := resScenario{name: fmt.Sprintf("%s/%s/%s", g, mix, set), world: w, spec: spec, good: g,
					targets: map[policy.Resource]int64{"Steel": 200, policy.ComponentResource: 10}, research: f.research,
					restoreTo: resFixtures[g].restore, days: 70, edgeDay: edge, lostDay: lost,
					checks: []string{chkSingle, chkTTL}}
				switch {
				case lost > 0:
					sc.checks = append(sc.checks, chkHold)
				case set == "requisition":
					sc.checks = append(sc.checks, chkEdge)
				default:
					sc.checks = append(sc.checks, chkRestore)
				}
				for _, s := range srcs {
					if spec[s.ID].Role == resDrill {
						sc.checks = append(sc.checks, chkGate)
						if len(srcs) == 1 && set == "none" {
							sc.checks = append(sc.checks, chkDrill)
						}
						break
					}
				}
				out = append(out, sc)
			}
		}
	}
	return out
}

// resRun is a finished scenario.
type resRun struct {
	sc  resScenario
	pl  *resPlanner
	rep supplysim.Report
}

func runResScenario(sc resScenario) resRun {
	pl := newResPlanner(sc.world, sc.spec, sc.targets, sc.research)
	return resRun{sc, pl, supplysim.Run(sc.world, pl, sc.days)}
}

// lastShockEnd is the day after which the world is quiet.
func (r resRun) lastShockEnd() int {
	end := 0
	for _, sh := range r.sc.world.Shocks {
		end = max(end, sh.Day+sh.Days)
	}
	return end
}

// verdict is the first violation of check, "" when it holds.
func (r resRun) verdict(check string) string {
	sc, pl := r.sc, r.pl
	switch check {
	case chkRestore:
		from := r.lastShockEnd()
		for d := from; d < min(from+restoreDays, len(r.rep.Days)); d++ {
			if r.rep.Days[d].Stock[sc.good] >= sc.restoreTo {
				return ""
			}
		}
		return fmt.Sprintf("%s stock stayed under %v for %d days after day %d (min runway %v)", sc.good, sc.restoreTo, restoreDays, from, r.rep.MinRunway[sc.good])
	case chkEdge:
		hit := false
		for _, e := range pl.events {
			if e.Day < sc.edgeDay {
				continue
			}
			if e.Good != sc.good {
				return fmt.Sprintf("day %d: %s source %s opened for a %s shortfall", e.Day, e.Good, e.Source, sc.good)
			}
			hit = hit || e.Day <= sc.edgeDay+2
		}
		for _, h := range pl.holds {
			// Work already open for the resource is the raised acquisition.
			hit = hit || h.Good == sc.good && h.Reason == "existing_work" && h.Day >= sc.edgeDay && h.Day <= sc.edgeDay+2
		}
		if !hit {
			return fmt.Sprintf("no %s acquisition within 2 days of the shortfall edge on day %d", sc.good, sc.edgeDay)
		}
	case chkSingle:
		seen := map[string]acquisitionBidder{}
		for _, e := range pl.events {
			key := fmt.Sprintf("%d/%s", e.Day, e.Good)
			if prev, ok := seen[key]; ok && prev != e.Bidder {
				return fmt.Sprintf("day %d: %s dispatched by %s and %s", e.Day, e.Good, prev, e.Bidder)
			}
			seen[key] = e.Bidder
		}
	case chkTTL:
		for _, y := range pl.yields {
			if y.Rival.tick+acquisitionBidTTL < y.Tick {
				return fmt.Sprintf("day %d: %s yielded to a bid %d ticks old", y.Day, y.Good, y.Tick-y.Rival.tick)
			}
		}
	case chkHold:
		holds := 0
		for _, h := range pl.holds {
			if h.Good == sc.good && h.Day > sc.lostDay && h.Reason == "no_source" {
				holds++
			}
		}
		if holds == 0 {
			return fmt.Sprintf("no no_source hold after the last source went on day %d", sc.lostDay)
		}
		for _, e := range pl.events {
			if e.Good == sc.good && e.Day > sc.lostDay {
				return fmt.Sprintf("day %d: opened %s with no source", e.Day, e.Source)
			}
		}
	case chkGate:
		for _, e := range pl.events {
			if e.Kind == policy.AcquisitionDeepDrill && !(e.Deficit && e.Research && e.Power > 0) {
				return fmt.Sprintf("day %d: drill without runway deficit, research and power", e.Day)
			}
		}
	case chkDrill:
		for _, s := range pl.drillScores {
			if s <= 0 {
				return "deep drill bid scores 0 (DeepDrillCandidate headroom is unknown, so no demand is credited)"
			}
		}
		if len(pl.drillScores) == 0 {
			return "deep drill never bid"
		}
	}
	return ""
}

func (r resRun) failures() map[string]string {
	out := map[string]string{}
	for _, c := range r.sc.checks {
		if v := r.verdict(c); v != "" {
			out[r.sc.name+"#"+c] = v
		}
	}
	return out
}

type resBaseline struct {
	Failing map[string]string `json:"failing"`
}

func readResourceBaseline(t *testing.T) resBaseline {
	var b resBaseline
	raw, err := os.ReadFile(resourceBaselinePath)
	if err != nil {
		if os.IsNotExist(err) {
			return resBaseline{Failing: map[string]string{}}
		}
		t.Fatal(err)
	}
	if err := json.Unmarshal(raw, &b); err != nil {
		t.Fatal(err)
	}
	return b
}

func TestResourceMatrixShrinksBaseline(t *testing.T) {
	t.Parallel()
	base := readResourceBaseline(t)
	scenarios := resScenarios()
	if len(scenarios) < 40 {
		t.Fatalf("matrix has %d scenarios", len(scenarios))
	}
	current := map[string]string{}
	for _, sc := range scenarios {
		for k, v := range runResScenario(sc).failures() {
			current[k] = v
		}
	}
	var added, fixed []string
	for k := range current {
		if _, known := base.Failing[k]; !known {
			added = append(added, k)
		}
	}
	for k := range base.Failing {
		if _, still := current[k]; !still {
			fixed = append(fixed, k)
		}
	}
	sort.Strings(added)
	sort.Strings(fixed)
	if *updateResourceBaseline {
		if len(added) > 0 && len(base.Failing) > 0 {
			t.Fatalf("the baseline only shrinks; new failures: %v", added)
		}
		next := resBaseline{Failing: current}
		raw, err := json.MarshalIndent(next, "", "  ")
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(resourceBaselinePath, append(raw, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
		return
	}
	for _, k := range added {
		t.Errorf("new failure %s: %s", k, current[k])
	}
	for _, k := range fixed {
		t.Errorf("baselined %s now passes: remove it from %s (go test -run TestResourceMatrixShrinksBaseline -update-resource-baseline)", k, resourceBaselinePath)
	}
}

// A healthy world restores its floor with each source: the matrix baseline
// records only genuine shortfalls, so the no-shock scenarios that pass prove
// the adapter reaches each acquisition kind.
func TestResourceMatrixReachesEveryAcquisitionKind(t *testing.T) {
	t.Parallel()
	kinds := map[policy.AcquisitionKind]bool{}
	for _, sc := range resScenarios() {
		for _, e := range runResScenario(sc).pl.events {
			kinds[e.Kind] = true
		}
	}
	for _, k := range []policy.AcquisitionKind{policy.AcquisitionChop, policy.AcquisitionMining, policy.AcquisitionProduce,
		policy.AcquisitionDeepDrill, policy.AcquisitionTrade, policy.AcquisitionLoot, policy.AcquisitionSalvage} {
		if !kinds[k] {
			t.Errorf("no scenario dispatched a %s acquisition", k)
		}
	}
}

// Urgent competing work holds an acquisition at the ranker (the planners pass
// no urgent priority today, so the hold is a ranker property).
func TestResourceMatrixUrgentCompetitionHoldsWork(t *testing.T) {
	t.Parallel()
	c, ok := policy.TradeCandidate("Steel", "trader", 100, 8)
	if !ok {
		t.Fatal("candidate")
	}
	demand := policy.ResourceDeficitDemand("Steel", 100)
	ranked, err := policy.RankResourceCandidates(demand, []policy.AcquisitionCandidate{c}, policy.AcquisitionCompetition{UrgentPriority: 50})
	if err != nil || len(ranked) != 0 {
		t.Fatalf("urgent work did not hold the purchase: %v %v", ranked, err)
	}
	s, _ := policy.ScoreResourceCandidate(demand, c, policy.AcquisitionCompetition{UrgentPriority: 50})
	if s.Hold != "competing_urgent_work" {
		t.Fatalf("hold %q", s.Hold)
	}
}

// A bid never holds a resource past its TTL: a stale rival is pruned and the
// resource planner dispatches.
func TestResourceMatrixStaleBidReleasesResource(t *testing.T) {
	t.Parallel()
	var b acquisitionBoard
	snap := domain.GenerationSnapshot{}
	b.bid(snap, "Steel", bidTrade, 9, policy.AcquisitionTrade, 1000)
	if _, yield := b.bid(snap, "Steel", bidResource, 1, policy.AcquisitionMining, 1000+acquisitionBidTTL); !yield {
		t.Fatal("a bid exactly at its TTL still holds")
	}
	if _, yield := b.bid(snap, "Steel", bidResource, 1, policy.AcquisitionMining, 1000+acquisitionBidTTL+1); yield {
		t.Fatal("a stale bid held the resource past its TTL")
	}
}

// Calibration against the step oracle: the adapter agrees with the planner on
// the remote-lump fixture (TestSteelDemandMinesTheRemoteLump...,
// TestDeliveredSteelPlansNoFurtherMining): with no steel a far lump is mined,
// with the floor met nothing is dispatched.
func TestResourceMatrixCalibratesRemoteLump(t *testing.T) {
	slowtest.Skip(t, "runs the resource planner step oracle under cmd/test -full and nightly")
	t.Parallel()
	for _, stock := range []int64{0, 200} {
		oracle, _ := remoteOreStep(t, stock)
		w := supplysim.World{Workers: 6, Stock: map[supplysim.Good]float64{supplysim.Steel: float64(stock)},
			Sources:   []supplysim.Source{supplysim.NewVein("lump", supplysim.Steel, 160, 20, 0)},
			Consumers: []supplysim.Consumer{{Good: supplysim.Steel, PerDay: 0}}}
		pl := newResPlanner(w, map[string]resSpec{"lump": {resMine, 90}}, map[policy.Resource]int64{"Steel": 200}, false)
		supplysim.Run(w, pl, 1)
		if got, want := pl.opens > 0, oracle.Verdict == BuildingReasonAdmitted; got != want {
			t.Fatalf("stock %d: adapter dispatched %v, planner admitted %v (%v)", stock, got, want, oracle)
		}
	}
}
