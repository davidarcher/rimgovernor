package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Bed replacement (#829): a solo bedroom below its RoomTarget whose owned
// bed is of poor quality (Awful or Poor) gets a new bed of the same
// definition. The game never places a bed over a standing one, so the
// replacement is one change walked across reviews, each step read back
// from the census rather than remembered:
//   - build: a PlaceBuilding for the new bed on free floor in the room;
//   - assign: once it stands, an AssignBed moves the owner onto it;
//   - remove: the old bed, now unowned and worse, is deconstructed; so
//     is a new bed that came out no better (the next epoch rebuilds).
//
// Material is not a lever: the bed census carries no stuff and the native
// build picks wood whenever the bed allows it, so a new bed can only
// improve on quality.

// bedQualityRank orders the native QualityCategory names.
var bedQualityRank = map[string]int{"Awful": 0, "Poor": 1, "Normal": 2, "Good": 3, "Excellent": 4, "Masterwork": 5, "Legendary": 6}

// replacementBedSizes are the North footprints of the beds the closer builds.
var replacementBedSizes = map[Resource]domain.Cell{"Bed": {X: 1, Z: 2}, "DoubleBed": {X: 2, Z: 2}, "RoyalBed": {X: 2, Z: 2}}

// BedReplacementStep is one step of a bed replacement.
type BedReplacementStep string

const (
	BedReplaceBuild  BedReplacementStep = "build"
	BedReplaceAssign BedReplacementStep = "assign"
	BedReplaceRemove BedReplacementStep = "remove"
)

// BedReplacement is the next bed replacement step. Build places Def at
// Cell/Rot; Assign moves Pawn from PreviousBed to Bed; Remove deconstructs
// Bed (Def at Cell).
type BedReplacement struct {
	Step             BedReplacementStep
	Room             string
	Pawn             PawnID
	Bed, PreviousBed string
	Def              string
	Cell             domain.Cell
	Rot              domain.Rotation
}

// bedBetter reports whether x improves on b for a room that wants the
// definition want: the wanted definition first, then quality.
func bedBetter(x, b SleepingBed, want Resource) bool {
	if x.Definition != want {
		return false
	}
	if b.Definition != want {
		return true
	}
	xq, xk := x.Quality.Value()
	bq, bk := b.Quality.Value()
	xr, xok := bedQualityRank[xq]
	br, bok := bedQualityRank[bq]
	return xk && bk && xok && bok && xr > br
}

// NextBedReplacement returns the first (by room id) bed replacement step
// due, false when none. rooms are the furniture rooms; available reports a
// definition the colony can build now.
func NextBedReplacement(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, available func(string) bool) (BedReplacement, bool) {
	census, ok := obs.Rooms.Value()
	if !ok {
		return BedReplacement{}, false
	}
	quality := map[string]RoomQuality{}
	for _, r := range census {
		if q, ok := r.Quality.Value(); ok {
			quality[r.ID] = q
		}
	}
	furniture := map[string]TidyRoom{}
	for _, r := range rooms {
		furniture[r.ID] = r
	}
	beds := map[string]SleepingBed{}
	inRoom := map[string][]SleepingBed{}
	for _, bed := range obs.Beds {
		beds[bed.ID] = bed
		room, ok := bed.Room.Value()
		humanlike, _ := bed.Humanlike.Value()
		medical, _ := bed.Medical.Value()
		prisoners, _ := bed.Prisoners.Value()
		if ok && room != "" && humanlike && !medical && !prisoners {
			inRoom[room] = append(inRoom[room], bed)
		}
	}
	titles := map[PawnID]*RoyalTitle{}
	for _, p := range obs.People {
		titles[p.ID] = p.Title
	}
	for _, s := range soloBedrooms(obs) {
		owned := beds[s.bed]
		want := owned.Definition
		if w, ok := titleBed(titles[s.owner], owned.Definition, available); ok {
			want = w
		}
		var spare []SleepingBed
		for _, b := range inRoom[s.room] {
			if b.ID != owned.ID && len(b.Owners) == 0 {
				spare = append(spare, b)
			}
		}
		for _, b := range spare {
			if bedBetter(b, owned, want) {
				return BedReplacement{Step: BedReplaceAssign, Room: s.room, Pawn: s.owner, Bed: b.ID, PreviousBed: owned.ID}, true
			}
		}
		for _, b := range spare {
			if !bedBetter(b, owned, want) {
				return BedReplacement{Step: BedReplaceRemove, Room: s.room, Bed: b.ID, Def: string(b.Definition), Cell: b.Cell}, true
			}
		}
		t, tk := targets[s.room]
		q, qk := quality[s.room]
		room, rk := furniture[s.room]
		if len(spare) > 0 || !rk {
			continue
		}
		if want != owned.Definition {
			// A royal title's bed requirement (#815) holds regardless of
			// the target.
			if cell, rot, ok := bedSpot(room, want); ok {
				return BedReplacement{Step: BedReplaceBuild, Room: s.room, Def: string(want), Cell: cell, Rot: rot}, true
			}
			continue
		}
		if !tk || !qk || t.NeverUpgrade || t.Min <= 0 || q.Impressiveness >= t.Min || (t.Max > 0 && q.Impressiveness >= t.Max) || WeakestRoomStat(q) == RoomStatSpace {
			continue
		}
		name, _ := owned.Quality.Value()
		if rank, ok := bedQualityRank[name]; !ok || rank >= bedQualityRank["Normal"] || !available(string(want)) {
			continue
		}
		if cell, rot, ok := bedSpot(room, want); ok {
			return BedReplacement{Step: BedReplaceBuild, Room: s.room, Def: string(want), Cell: cell, Rot: rot}, true
		}
	}
	return BedReplacement{}, false
}

// bedSpot is the first free anchor (back row first) where a bed of def
// fits the room's floor clear of its furniture and keeps it walkable.
func bedSpot(room TidyRoom, def Resource) (domain.Cell, domain.Rotation, bool) {
	size, ok := replacementBedSizes[def]
	if !ok {
		return domain.Cell{}, domain.South, false
	}
	return freeSpot(room, size)
}

// freeSpot is the first free anchor (back row first) where a piece of the
// North size fits the room's floor clear of its furniture and keeps it
// walkable.
func freeSpot(room TidyRoom, size domain.Cell) (domain.Cell, domain.Rotation, bool) {
	in := room.Room.Interior
	blocked := map[domain.Cell]bool{}
	for _, p := range room.Pieces {
		for _, c := range rectCells(p.Rect) {
			blocked[c] = true
		}
	}
	for z := in.Z + in.Height - 1; z >= in.Z; z-- {
		for x := in.X; x < in.X+in.Width; x++ {
			for _, rot := range []domain.Rotation{domain.South, domain.North} {
				anchor := domain.Cell{X: x, Z: z}
				r := OccupiedRect(anchor, size, rot)
				if r.Width == 0 || r.X < in.X || r.Z < in.Z || r.X+r.Width > in.X+in.Width || r.Z+r.Height > in.Z+in.Height {
					continue
				}
				cells := rectCells(r)
				free := true
				for _, c := range cells {
					free = free && !blocked[c]
				}
				if free && InteriorPlacementWalkable(room.Room, blocked, cells) {
					return anchor, rot, true
				}
			}
		}
	}
	return domain.Cell{}, domain.South, false
}

// titleBed is the bed a royal title requires (#815) when the owned bed
// does not meet it: the first buildable bed of the title's bed entry.
func titleBed(title *RoyalTitle, owned Resource, available func(string) bool) (Resource, bool) {
	if title == nil {
		return "", false
	}
	for _, thing := range title.BedroomThings {
		if !isReplacementBed(thing.AnyOf) {
			continue
		}
		for _, d := range thing.AnyOf {
			if d == owned {
				return "", false
			}
		}
		for _, d := range thing.AnyOf {
			if _, ok := replacementBedSizes[d]; ok && available(string(d)) {
				return d, true
			}
		}
	}
	return "", false
}

func isReplacementBed(defs []Resource) bool {
	for _, d := range defs {
		if _, ok := replacementBedSizes[d]; ok {
			return true
		}
	}
	return false
}
