package policy

import (
	"sort"
	"strconv"
)

// Companion beds. A bonded animal's master is its first bond partner
// by id on the roster (HerdMasterChoice). Each master with a solo
// bedroom gets one animal sleeping spot in it per animal they master, on
// free floor that keeps the room walkable; the bedroom closer places one per
// review, through the same room upgrade path as the title furniture. A
// master who shares a room, or has none, gets no spot: the animal sleeps
// where animals sleep.

// CompanionMaster is the colonist who masters a bonded animal: the first of
// its bond partners by id among people. False for an unbonded animal.
func CompanionMaster(a UpkeepAnimal, people []SleepingPerson) (PawnID, bool) {
	onRoster := map[PawnID]bool{}
	for _, p := range people {
		onRoster[p.ID] = true
	}
	var first PawnID
	for _, id := range a.BondedPawns {
		if pid := PawnID(id); onRoster[pid] && (first == "" || pid < first) {
			first = pid
		}
	}
	return first, first != ""
}

// NextCompanionBed returns the first companion bed due, false when none.
// rooms are the furniture rooms (FurnitureRooms); spot is the animal
// sleeping spot's shape and available reports that it can be built now.
func NextCompanionBed(obs SleepingObservation, animals []UpkeepAnimal, rooms []FurnitureRoom, beds RoomFurniture, spot InteriorPieceDef, available bool) (RoomUpgrade, bool) {
	if !available || spot.Def == "" {
		return RoomUpgrade{}, false
	}
	owed := map[PawnID]int{}
	for _, a := range animals {
		release, _ := a.Release.Value()
		slaughter, _ := a.Slaughter.Value()
		if master, ok := CompanionMaster(a, obs.People); ok && !release && !slaughter {
			owed[master]++
		}
	}
	furniture := map[string]FurnitureRoom{}
	for _, r := range rooms {
		furniture[r.ID] = r
	}
	solo := soloBedrooms(obs)
	sort.Slice(solo, func(i, j int) bool { return solo[i].room < solo[j].room })
	for _, s := range solo {
		room, ok := furniture[s.room]
		if !ok || owed[s.owner] == 0 {
			continue
		}
		have := 0
		for _, p := range room.Pieces {
			if p.Def == beds.AnimalSpot || p.Def == beds.AnimalBed {
				have++
			}
		}
		if have >= owed[s.owner] {
			continue
		}
		if cell, rot, ok := freeSpot(room, spot.Size); ok {
			return RoomUpgrade{Room: s.room, Slot: "companion-bed-" + strconv.Itoa(have+1), Def: spot.Def, Anchor: cell, Rot: rot}, true
		}
	}
	return RoomUpgrade{}, false
}
