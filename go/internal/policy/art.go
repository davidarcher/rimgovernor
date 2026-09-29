package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainArt keeps a small sculpture on the way for every room that needs
// one (#1190, epic #1172): a bedroom below its quality target whose weakest
// stat is beauty. Each qualifying artist gets their own fixed-count bill,
// pinned to them, at an art bench; the finished sculpture is installed by
// the bedroom upkeep's sculpture step.
const MaintainArt GoalID = "MaintainArt"

// artPriority ranks MaintainArt with the other upkeep projects.
const artPriority = 3

// ArtBill is MaintainArt's bill purpose: one small sculpture pinned to the
// artist named in ProductionBillContext.Artists.
const ArtBill BillPurpose = "art"

// Artist reports whether a pawn qualifies for an art bill: Artistic above
// 6 or any Artistic passion, and able to do Art at all.
func Artist(p PawnProfile) bool {
	if p.Incapable[WorkArt] || p.Child {
		return false
	}
	s := p.Skill(WorkSkillName(WorkArt))
	return !s.Disabled && (s.Level > 6 || s.Passion != "")
}

// Artists lists the qualifying artists in ID order.
func Artists(profiles []PawnProfile) []PawnID {
	var out []PawnID
	for _, p := range profiles {
		if Artist(p) {
			out = append(out, p.ID)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// SelectArtBills returns one pinned sculpture bill for every artist that
// lacks an active one, in artist order, each through SelectProductionBill.
func SelectArtBills(benches domain.Fact[[]ProductionBench], colonists domain.Fact[int64], artists []PawnID) []BillSelection {
	var out []BillSelection
	for _, artist := range artists {
		if s, ok := SelectProductionBill(ArtBill, benches, colonists, domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{Artists: []PawnID{artist}}); ok {
			out = append(out, s)
		}
	}
	return out
}

// InspiredCreativity is the InspirationDef an inspired artist carries
// (#1192); their next art piece gets a quality boost.
const InspiredCreativity = "Inspired_Creativity"

// InspiredArtRecipe is the priority bill an inspired artist gets: a large
// sculpture. Grand sculptures wait for the stuff choice (#1191).
const InspiredArtRecipe = "Make_SculptureLarge"

// InspiredArtists lists the qualifying artists with Inspired_Creativity, in
// ID order.
func InspiredArtists(profiles []PawnProfile) []PawnID {
	var out []PawnID
	for _, p := range profiles {
		if inspiration, known := p.Inspiration.Value(); known && inspiration == InspiredCreativity && Artist(p) {
			out = append(out, p.ID)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// SelectInspiredArtBills returns one large sculpture bill pinned to every
// inspired artist lacking an active one (#1192). The caller orders these
// ahead of SelectArtBills so the inspiration is spent before it expires.
func SelectInspiredArtBills(benches domain.Fact[[]ProductionBench], inspired []PawnID) []BillSelection {
	rows, known := benches.Value()
	if !known {
		return nil
	}
	var out []BillSelection
	for _, artist := range inspired {
		if !foodID(string(artist)) {
			continue
		}
		if s, ok := selectPinnedSculpture(rows, artist, InspiredArtRecipe); ok {
			out = append(out, s)
		}
	}
	return out
}

// selectArtBill picks the art bench for one artist's sculpture bill, none
// while the artist already has an active sculpture bill pinned to them.
func selectArtBill(benches []ProductionBench, artist PawnID) (BillSelection, bool) {
	return selectPinnedSculpture(benches, artist, SculptureRecipe)
}

// selectPinnedSculpture picks the art bench for one artist's recipe bill,
// none while the artist already has an active bill for recipe pinned to
// them.
func selectPinnedSculpture(benches []ProductionBench, artist PawnID, recipeName string) (BillSelection, bool) {
	if !foodID(string(artist)) {
		return BillSelection{}, false
	}
	var options []BillSelection
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			worker, wk := bill.Worker.Value()
			active, ak := bill.Active.Value()
			if bill.Recipe == recipeName && wk && worker == string(artist) && (!ak || active) {
				return BillSelection{}, false
			}
		}
		usable, uk := bench.Usable.Value()
		token, tk := bench.Token.Value()
		if !uk || !usable || !tk || !foodID(token) || len(bench.Bills) >= 15 {
			continue
		}
		for _, recipe := range bench.Recipes {
			if available, ak := recipe.Available.Value(); recipe.Name == recipeName && ak && available {
				options = append(options, BillSelection{Bench: bench.ID, Recipe: recipeName, Token: token, Mode: domain.GearBatch, Target: 1, Worker: string(artist)})
			}
		}
	}
	if len(options) == 0 {
		return BillSelection{}, false
	}
	sort.Slice(options, func(i, j int) bool { return options[i].Bench < options[j].Bench })
	return options[0], true
}

// SculptureRoomsOwed is the review's art deficit input: known true while a
// bedroom below target, weakest in beauty, has a free cell for a sculpture.
func SculptureRoomsOwed(obs domain.Fact[SleepingObservation], targets map[string]RoomTarget, rooms []TidyRoom) domain.Fact[bool] {
	o, known := obs.Value()
	if !known || targets == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(sculptureRooms(o, targets, rooms)) > 0)
}

// sculptureRooms are the beauty rooms (by id) with a free cell, each with
// that cell.
func sculptureRooms(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom) []sculptureRoom {
	var out []sculptureRoom
	for _, id := range beautyRooms(obs, targets) {
		for _, room := range rooms {
			if room.ID != id {
				continue
			}
			if cell, _, ok := freeSpot(room, domain.Cell{X: 1, Z: 1}); ok {
				out = append(out, sculptureRoom{id, cell})
			}
			break
		}
	}
	return out
}

type sculptureRoom struct {
	ID   string
	Cell domain.Cell
}
