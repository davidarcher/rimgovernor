package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Royal bedroom requirements: a titled owner's solo bedroom must
// hold the title's BedroomThings, each met by Count things of any one of
// its definitions. The bed entry is the bed replacement's (titleBed); every
// other entry is placed here, one piece at a time, at its bedroom template
// slot when the template plans that definition and the slot is free, or
// else on free floor. Minimum impressiveness is the room target's "title"
// reason and flooring is MaintainFlooring's living tier; the minimum area
// is not a lever (a room is not enlarged).

// titleFurnitureSizes are the North footprints of the non-bed royal
// bedroom things the closer places.
var titleFurnitureSizes = map[Resource]domain.Cell{"EndTable": {X: 1, Z: 1}, "Dresser": {X: 2, Z: 1}}

// NextTitleFurniture returns the first unmet royal bedroom thing, false
// when none.
func NextTitleFurniture(obs SleepingObservation, rooms []FurnitureRoom, available func(string) bool) (RoomUpgrade, bool) {
	titles := map[PawnID]*RoyalTitle{}
	for _, p := range obs.People {
		titles[p.ID] = p.Title
	}
	furniture := map[string]FurnitureRoom{}
	for _, r := range rooms {
		furniture[r.ID] = r
	}
	for _, s := range soloBedrooms(obs) {
		title := titles[s.owner]
		room, ok := furniture[s.room]
		if title == nil || !ok {
			continue
		}
		for _, thing := range title.BedroomThings {
			if isReplacementBed(thing.AnyOf) {
				continue
			}
			have := 0
			for _, p := range room.Pieces {
				for _, d := range thing.AnyOf {
					if p.Def == string(d) {
						have++
					}
				}
			}
			if have >= thing.Count {
				continue
			}
			for _, d := range thing.AnyOf {
				if u, ok := titlePiece(s.room, room, d, available); ok {
					return u, true
				}
			}
		}
	}
	return RoomUpgrade{}, false
}

func titlePiece(id string, room FurnitureRoom, def Resource, available func(string) bool) (RoomUpgrade, bool) {
	size, known := titleFurnitureSizes[def]
	if !known || !available(string(def)) {
		return RoomUpgrade{}, false
	}
	slot := "title-" + string(def)
	if plan, ok := PlanInterior(room.Room, InteriorPieceDef{}); ok {
		for _, p := range plan.Pieces {
			if p.Def == string(def) && !roomPieceOverlaps(room.Pieces, p.Rect) {
				return RoomUpgrade{Room: id, Slot: slot, Def: p.Def, Anchor: p.Anchor(), Rot: p.Rot}, true
			}
		}
	}
	if cell, rot, ok := freeSpot(room, size); ok {
		return RoomUpgrade{Room: id, Slot: slot, Def: string(def), Anchor: cell, Rot: rot}, true
	}
	return RoomUpgrade{}, false
}
