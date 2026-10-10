package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// MaintainArt declares its pinned sculpture batches to the work ledger
// (OrderDeclarer): the ledger places a missing one, keeps a standing one and
// removes the rest, so a sculpture pinned to someone who is no longer an artist
// is simply no longer declared. Sculptures to sell are MaintainTrade's.

// ArtOrderRequest is what one Round's sculpture declaration reads: the bench
// readback the ledger reconciles against, the work pawns' profiles, the art
// demand and whether a bedroom still wants a sculpture.
type ArtOrderRequest struct {
	Benches   []GearBench
	Profiles  domain.Fact[[]PawnProfile]
	Colonists domain.Fact[int64]
	Items     ItemFacts
	Demand    ArtDemand
	RoomsOwed domain.Fact[bool]
}

// DeclareArtOrders is MaintainArt's wanted sculpture orders. While the need is
// open (an inspired artist or a bedroom below target) every active sculpture bill pinned to a current artist is declared as
// it stands, and each artist lacking one is given the bill the art selectors
// choose; a need known to be gone declares nothing, so the ledger removes
// the sculptures. Abstain while the artists or the need are unread, or a
// standing sculpture bill's spec is.
func DeclareArtOrders(r ArtOrderRequest) Declared {
	profiles, known := r.Profiles.Value()
	if !known {
		return Abstaining(UnreadArtists)
	}
	artists, inspired := Artists(profiles), InspiredArtists(profiles)
	owed, owedKnown := r.RoomsOwed.Value()
	sculpt := owedKnown && owed
	open := sculpt || len(inspired) > 0
	if !open && owedKnown {
		return Declared{}
	}
	orders, unread := standingSculptures(r.Benches, artists)
	out := Declared{Orders: orders}
	if unread {
		out.Unread(UnreadBills)
	}
	if !open {
		out.Unread(UnreadArtNeed)
	}
	benches := domain.Known(ArtProductionBenches(r.Benches))
	selected := SelectInspiredArtBills(benches, inspired, r.Items)
	if sculpt {
		selected = append(selected, SelectArtBills(benches, r.Colonists, artists, r.Demand)...)
	}
	for _, s := range selected {
		out.Orders = append(out.Orders, OrderSpec{Recipe: s.Recipe, Ingredients: s.Ingredients, Worker: s.Worker, Mode: s.Mode, Target: s.Target, BenchKind: gearBenchDef(r.Benches, s.Bench)})
	}
	return out
}

// standingSculptures are the specs of the active (or unknown-active) sculpture
// bills pinned to a current artist; unread is set when one's spec is.
func standingSculptures(benches []GearBench, artists []PawnID) (orders []OrderSpec, unread bool) {
	current := map[string]bool{}
	for _, a := range artists {
		current[string(a)] = true
	}
	for _, b := range benches {
		bills, _ := b.Bills.Value()
		for _, bill := range bills {
			worker, wk := bill.Worker.Value()
			if bill.Role != domain.RoleSculpture || !wk || !current[worker] {
				continue
			}
			if active, ak := bill.Active.Value(); ak && !active {
				continue
			}
			spec, sk := bill.Spec.Value()
			if !sk {
				unread = true
				continue
			}
			orders = append(orders, spec)
		}
	}
	return orders, unread
}

// ArtProductionBenches are the benches offering a sculpture recipe as the
// art selectors read them; a bench whose recipes or bills are unknown is left
// out. The ledger chooses the bench when it places a declared order, so the
// bench id stands in for the write token no declaration needs.
func ArtProductionBenches(benches []GearBench) []ProductionBench {
	var out []ProductionBench
	for _, read := range benches {
		recipes, rk := read.Recipes.Value()
		bills, bk := read.Bills.Value()
		if !rk || !bk {
			continue
		}
		bench := ProductionBench{ID: read.ID, Token: domain.Known(read.ID), Usable: domain.Known(true)}
		for _, recipe := range recipes {
			if recipe.Role != domain.RoleSculpture {
				continue
			}
			available, ak := recipe.Available.Value()
			on, ok := recipe.AvailableOn.Value()
			row := ProductionRecipe{Name: recipe.Definition, Role: recipe.Role}
			if ak && ok {
				row.Available = domain.Known(available && on)
			}
			bench.Recipes = append(bench.Recipes, row)
		}
		if len(bench.Recipes) == 0 {
			continue
		}
		for _, bill := range bills {
			bench.Bills = append(bench.Bills, ExistingProductionBill{ID: bill.ID, Recipe: bill.Recipe, Role: bill.Role, Worker: bill.Worker, Active: bill.Active})
		}
		out = append(out, bench)
	}
	return out
}

// SculptureInProgress reports whether any sculpture bill is active: the
// sculpture takes days of game time that a placed bill does not ask the clock
// for.
func SculptureInProgress(benches []GearBench) bool {
	for _, b := range benches {
		bills, _ := b.Bills.Value()
		for _, bill := range bills {
			if active, known := bill.Active.Value(); known && active && bill.Role == domain.RoleSculpture {
				return true
			}
		}
	}
	return false
}
