package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainMechs is the mech gestation goal (#1686, epic #1667): a mechanitor
// with bandwidth to spare, an idle mech gestator and no waste left lying
// about is owed one more mech, queued as a Bill_Mech on the gestator.
const MaintainMechs GoalID = "MaintainMechs"

// mechPriority ranks MaintainMechs with the other upkeep projects.
const mechPriority = 3

// MechGestationBill is MaintainMechs' bill purpose: one single-count
// gestation bill (domain.GearBatch, target 1) on a gestator.
const MechGestationBill BillPurpose = "mech_gestation"

// Gestation rules (epic #1667 research: Biotech, Mechanitor and wastepack
// guides on the wiki; numbers come from the game defs, never from here):
//   - Bandwidth is the limit. A mech is built only when a mechanitor's free
//     bandwidth, TotalBandwidth - UsedBandwidth - GestationBandwidth, covers
//     its cost (the catalog's MechKindRow bandwidth_cost). The sum is
//     deliberately conservative: it never promises more than the game's own
//     HasBandwidthForBill, which native re-checks at apply.
//   - Every gestation produces wastepacks, and an unfrozen pack deteriorates
//     into pollution. New gestation is held while the gestators hold their
//     own waste or any wastepack is uncleared: not frozen, not in an
//     atomizer, and not dissolved. Unknown waste holds too.
//   - Colonist need decides what the free bandwidth buys (MechRoleNext): a
//     worker while a work type is short of owners and no mech of the
//     mechanitor covers it, a guard otherwise.
//   - Chargers come before more mechs (epic #1667 research: guides recommend a
//     recharger before more mechs): no gestation while no charger is ready,
//     powered and not full of waste (MechChargerReady), so the colony never
//     builds a mech it cannot charge. EnsureMechCharger builds the charger.
//   - One gestation at a time: a standing gestation bill, or a gestator
//     with an active bill, is in production and gets no sibling, so the
//     bandwidth the next bill spends is never spent twice.

// GestatorFact is one gestator of the colony section.
type GestatorFact struct {
	ID string
	// Active is whether the gestator has an active bill (a mech forming).
	Active domain.Fact[bool]
	// WasteCount is the wastepacks held in its own waste producer.
	WasteCount domain.Fact[int32]
}

// WastepackFact is one stack of wastepacks as the colony section reads it.
type WastepackFact struct {
	Count              int32
	Frozen, InAtomizer domain.Fact[bool]
}

// MechGestation is what the gestation goal decides from: the catalog, the
// colony's mechanitors and mechs, the work coverage, and the gestators and
// wastepacks of the Biotech colony section, and the chargers.
type MechGestation struct {
	Catalog     MechCatalog
	Mechanitors []MechanitorInput
	Mechs       []MechInput
	Coverage    []WorkCoverage
	Gestators   []GestatorFact
	Wastepacks  []WastepackFact
	Chargers    []MechCharger
}

// UnclearedWaste reports whether new gestation must wait on waste: a
// gestator holding waste, or a wastepack stack not frozen and not in an
// atomizer (a dissolved pack is no stack any more; an empty stack counts as
// dissolved). An unread count or flag cannot certify the waste cleared.
func UnclearedWaste(g MechGestation) bool {
	for _, gestator := range g.Gestators {
		if n, ok := gestator.WasteCount.Value(); !ok || n > 0 {
			return true
		}
	}
	for _, pack := range g.Wastepacks {
		if pack.Count <= 0 {
			continue
		}
		if frozen, ok := pack.Frozen.Value(); ok && frozen {
			continue
		}
		if atomizer, ok := pack.InAtomizer.Value(); ok && atomizer {
			continue
		}
		return true
	}
	return false
}

// FreeBandwidth is a mechanitor's bandwidth left for a new mech; ok is
// false while any of the three figures is unread.
func FreeBandwidth(m PawnMechanitor) (free int, ok bool) {
	total, tk := m.TotalBandwidth.Value()
	used, uk := m.UsedBandwidth.Value()
	gestation, gk := m.GestationBandwidth.Value()
	if !tk || !uk || !gk {
		return 0, false
	}
	return total - used - gestation, true
}

// MechChoice is the mech kind the next gestation builds and who it is for.
type MechChoice struct {
	Mechanitor PawnID
	Kind       string
}

// NextMech picks the next mech: for each mechanitor, most free bandwidth
// first, the role MechRoleNext names, then the best kind of that role the
// mechanitor can afford and offered says a gestator can make. Workers rank by
// how many short, uncovered work types the kind covers; guards by combat
// power per bandwidth; ties fall to the lower cost, then the name. ok is
// false with nothing affordable. A kind or mech the catalog lacks is an
// error.
func NextMech(g MechGestation, offered func(kind string) bool) (MechChoice, bool, error) {
	ordered := slices.Clone(g.Mechanitors)
	sort.Slice(ordered, func(i, j int) bool {
		fi, _ := FreeBandwidth(ordered[i].PawnMechanitor)
		fj, _ := FreeBandwidth(ordered[j].PawnMechanitor)
		if fi != fj {
			return fi > fj
		}
		return ordered[i].ID < ordered[j].ID
	})
	for _, m := range ordered {
		free, ok := FreeBandwidth(m.PawnMechanitor)
		if !ok || free <= 0 {
			continue
		}
		role, ok, err := MechRoleNext(g.Catalog, m, g.Mechs, g.Coverage)
		if err != nil {
			return MechChoice{}, false, err
		}
		if !ok {
			continue
		}
		short, err := shortUncoveredWork(g.Catalog, m, g.Mechs, g.Coverage)
		if err != nil {
			return MechChoice{}, false, err
		}
		var kinds []MechKind
		for _, kind := range g.Catalog.Kinds {
			if kind.Role() == role && kind.BandwidthCost > 0 && kind.BandwidthCost <= float64(free) && offered(kind.Name) {
				kinds = append(kinds, kind)
			}
		}
		score := func(k MechKind) float64 {
			if role == MechGuard {
				return k.CombatPower / k.BandwidthCost
			}
			n := 0
			for _, w := range k.WorkTypes {
				if short[w] {
					n++
				}
			}
			return float64(n)
		}
		// A worker is bought for the short work it covers; one that covers
		// none of it answers no need.
		if role == MechWorker {
			kinds = slices.DeleteFunc(kinds, func(k MechKind) bool { return score(k) == 0 })
		}
		if len(kinds) == 0 {
			continue
		}
		sort.Slice(kinds, func(i, j int) bool {
			a, b := kinds[i], kinds[j]
			if sa, sb := score(a), score(b); sa != sb {
				return sa > sb
			}
			if a.BandwidthCost != b.BandwidthCost {
				return a.BandwidthCost < b.BandwidthCost
			}
			return a.Name < b.Name
		})
		return MechChoice{Mechanitor: m.ID, Kind: kinds[0].Name}, true, nil
	}
	return MechChoice{}, false, nil
}

// MechGestationOwed is the goal's deficit: false while waste is uncleared
// (or unread), no charger is ready, a gestator is forming a mech, or no gestator exists; true
// when NextMech finds an affordable mech of the needed role. The catalog's
// kinds stand in for the recipes a gestator offers here; the bill planner
// narrows to the recipes it can actually queue.
func MechGestationOwed(g MechGestation) (domain.Fact[bool], error) {
	if len(g.Gestators) == 0 || UnclearedWaste(g) || !MechChargerReady(g.Chargers) {
		return domain.Known(false), nil
	}
	for _, gestator := range g.Gestators {
		if active, ok := gestator.Active.Value(); !ok || active {
			return domain.Known(false), nil
		}
	}
	_, ok, err := NextMech(g, func(string) bool { return true })
	if err != nil {
		return domain.Unknown[bool](), err
	}
	return domain.Known(ok), nil
}

// SelectMechGestationBill queues the next mech's recipe on the best usable
// gestator: the recipe whose MechKind is the choice, bulk recipes first,
// then by recipe and bench name. When it queues nothing the gap says why:
// uncleared waste, no ready charger, a gestation already in production, no
// usable recipe for the mech wanted, full bench bill lists, or no mech
// affordable (nothing wanted).
func SelectMechGestationBill(benches []ProductionBench, g MechGestation) (BillSelection, BillGap, error) {
	if UnclearedWaste(g) {
		return BillSelection{}, BillGapWaste, nil
	}
	if !MechChargerReady(g.Chargers) {
		return BillSelection{}, BillGapNoCharger, nil
	}
	gestation := map[string]bool{}
	for _, bench := range benches {
		for _, recipe := range bench.Recipes {
			if recipe.MechKind != "" {
				gestation[recipe.Name] = true
			}
		}
	}
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			// A standing gestation bill, active or not, is in production.
			if gestation[bill.Recipe] {
				return BillSelection{}, BillGapInProduction, nil
			}
		}
	}
	available := map[string]bool{}
	for _, bench := range benches {
		for _, recipe := range bench.Recipes {
			if ok, known := recipe.Available.Value(); known && ok && recipe.MechKind != "" && usableBench(bench) {
				available[recipe.MechKind] = true
			}
		}
	}
	for kind := range available {
		if _, known := g.Catalog.Kinds[kind]; !known {
			return BillSelection{}, "", fmt.Errorf("gestation recipe makes mech kind %s that is not in the catalog", kind)
		}
	}
	choice, ok, err := NextMech(g, func(kind string) bool { return available[kind] })
	if err != nil {
		return BillSelection{}, "", err
	}
	if !ok {
		// A mech is wanted but no recipe on a usable bench makes it, or no
		// mech is affordable at all.
		_, wanted, err := NextMech(g, func(string) bool { return true })
		if err != nil {
			return BillSelection{}, "", err
		}
		if wanted {
			return BillSelection{}, BillGapNoRecipe, nil
		}
		return BillSelection{}, BillGapNothingWanted, nil
	}
	var options []BillSelection
	bulk := map[string]bool{}
	for _, bench := range benches {
		if !usableBench(bench) || len(bench.Bills) >= 15 {
			continue
		}
		token, _ := bench.Token.Value()
		for _, recipe := range bench.Recipes {
			if ok, known := recipe.Available.Value(); known && ok && recipe.MechKind == choice.Kind {
				options = append(options, BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.GearBatch, Target: 1})
				bulk[recipe.Name] = recipe.Bulk
			}
		}
	}
	if len(options) == 0 {
		return BillSelection{}, BillGapBenchFull, nil
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if bulk[a.Recipe] != bulk[b.Recipe] {
			return bulk[a.Recipe]
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		return a.Bench < b.Bench
	})
	return options[0], "", nil
}

func usableBench(b ProductionBench) bool {
	usable, uk := b.Usable.Value()
	token, tk := b.Token.Value()
	return uk && usable && tk && foodID(token)
}
