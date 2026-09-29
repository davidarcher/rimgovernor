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
func SelectArtBills(benches domain.Fact[[]ProductionBench], colonists domain.Fact[int64], artists []PawnID, demand ArtDemand) []BillSelection {
	var out []BillSelection
	for _, artist := range artists {
		if s, ok := SelectProductionBill(ArtBill, benches, colonists, domain.Fact[float64]{}, domain.Fact[float64]{}, 1, ProductionBillContext{Artists: []PawnID{artist}, Art: &demand}); ok {
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

// selectArtBill picks the art bench, size and stuff for one artist's
// sculpture bill (#1191), none while the artist already has an active
// sculpture bill pinned to them.
func selectArtBill(benches []ProductionBench, artist PawnID, demand ArtDemand) (BillSelection, bool) {
	available, pinned := pinnedSculptureOptions(benches, artist)
	if len(pinned) > 0 {
		return BillSelection{}, false
	}
	for _, size := range sculptureSizes {
		options := available[size.Recipe]
		if len(options) == 0 {
			continue
		}
		stuff, ok := demand.stuff(size)
		if size.Recipe != SculptureRecipe && (!ok || demand.Gap < size.MinGap || demand.Fits < size.Size.X || demand.Skill[artist] < size.MinSkill) {
			continue
		}
		if ok {
			options[0].Ingredients = []string{string(stuff)}
		}
		return options[0], true
	}
	return BillSelection{}, false
}

// selectPinnedSculpture picks the art bench for one artist's recipe bill,
// none while the artist already has an active bill for recipe pinned to
// them.
func selectPinnedSculpture(benches []ProductionBench, artist PawnID, recipeName string) (BillSelection, bool) {
	available, pinned := pinnedSculptureOptions(benches, artist)
	if pinned[recipeName] || len(available[recipeName]) == 0 {
		return BillSelection{}, false
	}
	return available[recipeName][0], true
}

// pinnedSculptureOptions are the bills (by recipe, bench order) the artist
// could be given and the sculpture recipes of the active bills already
// pinned to them.
func pinnedSculptureOptions(benches []ProductionBench, artist PawnID) (map[string][]BillSelection, map[string]bool) {
	available, pinned := map[string][]BillSelection{}, map[string]bool{}
	if !foodID(string(artist)) {
		return available, pinned
	}
	for _, bench := range benches {
		for _, bill := range bench.Bills {
			worker, wk := bill.Worker.Value()
			active, ak := bill.Active.Value()
			if IsSculptureRecipe(bill.Recipe) && wk && worker == string(artist) && (!ak || active) {
				pinned[bill.Recipe] = true
			}
		}
		usable, uk := bench.Usable.Value()
		token, tk := bench.Token.Value()
		if !uk || !usable || !tk || !foodID(token) || len(bench.Bills) >= 15 {
			continue
		}
		for _, recipe := range bench.Recipes {
			if ok, ak := recipe.Available.Value(); IsSculptureRecipe(recipe.Name) && ak && ok {
				available[recipe.Name] = append(available[recipe.Name], BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.GearBatch, Target: 1, Worker: string(artist)})
			}
		}
	}
	for _, options := range available {
		sort.Slice(options, func(i, j int) bool { return options[i].Bench < options[j].Bench })
	}
	return available, pinned
}

// Sculpture sizes (#1191), largest first: the recipe, the built
// definition, its footprint and stuff cost (vanilla
// ThingDefs_Buildings/Buildings_Art.xml), and when an art bill may choose
// it: the owed room's impressiveness gap, a free square the size of the
// footprint, stock for it, and an artist skill that expects at least a
// Normal (Large) or Good (Grand) piece. The small sculpture is always
// allowed, in the best stocked stuff when there is one.
type sculptureSize struct {
	Recipe, Def string
	Size        domain.Cell
	Cost        int64
	MinGap      float64
	MinSkill    int
}

var sculptureSizes = []sculptureSize{
	{Recipe: "Make_SculptureGrand", Def: "SculptureGrand", Size: domain.Cell{X: 3, Z: 3}, Cost: 400, MinGap: 25, MinSkill: 10},
	{Recipe: "Make_SculptureLarge", Def: "SculptureLarge", Size: domain.Cell{X: 2, Z: 2}, Cost: 150, MinGap: 10, MinSkill: 6},
	{Recipe: SculptureRecipe, Def: SculptureDefinition, Size: domain.Cell{X: 1, Z: 1}, Cost: 50},
}

// IsSculptureRecipe reports whether recipe is one of the art bill's
// sculpture recipes.
func IsSculptureRecipe(recipe string) bool {
	for _, s := range sculptureSizes {
		if s.Recipe == recipe {
			return true
		}
	}
	return false
}

// SculptureSize is the North footprint of a sculpture definition.
func SculptureSize(def string) (domain.Cell, bool) {
	for _, s := range sculptureSizes {
		if s.Def == def {
			return s.Size, true
		}
	}
	return domain.Cell{}, false
}

// ArtDemand is what an art bill sizes against (#1191): the impressiveness
// gap of the first owed room and the largest square side (1-3) free in it,
// the colony stock by definition, and each artist's Artistic level.
type ArtDemand struct {
	Gap   float64
	Fits  int32
	Stock map[Resource]int64
	Skill map[PawnID]int
}

// stuff is the best stocked stuff (beauty factor times market value, as
// beds choose) with enough for one sculpture of size, false when none.
func (d ArtDemand) stuff(size sculptureSize) (Resource, bool) {
	return BedMaterials{Stock: d.Stock, Cost: map[Resource]int64{Resource(size.Def): size.Cost}}.bestStuff(Resource(size.Def), 0, true)
}

// NewArtDemand reads the art demand from the first owed sculpture room and
// the artists' profiles.
func NewArtDemand(obs domain.Fact[SleepingObservation], targets map[string]RoomTarget, rooms []TidyRoom, stock map[Resource]int64, profiles []PawnProfile) ArtDemand {
	d := ArtDemand{Stock: stock, Skill: map[PawnID]int{}}
	for _, p := range profiles {
		d.Skill[p.ID] = p.Skill(WorkSkillName(WorkArt)).Level
	}
	o, known := obs.Value()
	if !known || targets == nil {
		return d
	}
	due := sculptureRooms(o, targets, rooms)
	if len(due) == 0 {
		return d
	}
	d.Gap, d.Fits = due[0].Gap, 1
	for side := int32(3); side > 1; side-- {
		if _, _, ok := freeSpot(due[0].Room, domain.Cell{X: side, Z: side}); ok {
			d.Fits = side
			break
		}
	}
	return d
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
// its impressiveness gap.
func sculptureRooms(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom) []sculptureRoom {
	var out []sculptureRoom
	gaps := map[string]float64{}
	if census, ok := obs.Rooms.Value(); ok {
		for _, r := range census {
			if q, qk := r.Quality.Value(); qk {
				gaps[r.ID] = targets[r.ID].Min - q.Impressiveness
			}
		}
	}
	for _, id := range beautyRooms(obs, targets) {
		for _, room := range rooms {
			if room.ID != id {
				continue
			}
			if _, _, ok := freeSpot(room, domain.Cell{X: 1, Z: 1}); ok {
				out = append(out, sculptureRoom{ID: id, Room: room, Gap: gaps[id]})
			}
			break
		}
	}
	return out
}

type sculptureRoom struct {
	ID   string
	Room TidyRoom
	Gap  float64
}
