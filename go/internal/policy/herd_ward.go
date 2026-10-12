package policy

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The hospital's animal ward. A sick animal is treated in the hospital, not in a
// room of the herd's: a wound's infection chance reads the room the patient
// lies in (cleanliness), a barn with sixteen cows is filthy, and surgery
// applies the bed's success factor only to a patient lying in a bed. The game
// gives an animal no human bed (RestUtility.CanUseBedEver wants the bed's
// bed_humanlike to match the pawn's), so the hospital also holds AnimalBeds:
// beds that are not humanlike, so they neither change the hospital's room role
// nor count as a patient bed (SelectHospitalBed counts humanlike beds only).
//
// The ward is the hospital's free floor beside its human template (planHospital)
// that holds an animal bed and keeps the room walkable; it is sized from the
// animals that need it (WardBeds) and the slots are whatever the room has left.
// Each standing ward bed is flagged medical (a BedUse patch; any bed can carry
// the flag natively), so colonists treat animals in it.

// HospitalAreaKey is the bot area key of the Hospital allowed area: the interior
// of the plan's hospitals. It is the area SterilizeChoice lets an animal into
// for surgery.
const HospitalAreaKey = "Hospital"

// HospitalAreaLabel is the native label of the Hospital area.
const HospitalAreaLabel = HospitalAreaKey

// HospitalCells are the interior cells of the plan's hospitals, sorted.
func (p LayoutPlan) HospitalCells() []domain.Cell {
	set := map[domain.Cell]bool{}
	for _, room := range p.hospitals() {
		for _, c := range rectCells(room.Region.Bounds()) {
			set[c] = true
		}
	}
	return sortedCells(set)
}

// hospitals are the plan's hospital rooms, in plan order.
func (p LayoutPlan) hospitals() []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.Rooms {
		if r.Role == PlannedHospital {
			out = append(out, r)
		}
	}
	return out
}

// AnimalWard is what the layout exposes of the hospital's animal ward to the
// herd plan. Ready is a standing hospital with a built medical animal bed;
// Area is the allowed-area id covering the hospital, the area an animal is let
// into for surgery. Either unknown (or Ready false) means no sterilize.
type AnimalWard struct {
	Ready domain.Fact[bool]
	Area  domain.Fact[string]
}

// WardBeds is the animal beds the hospital is asked for: one per animal
// that needs tending now (a condition not yet tended, or a life threat), and one
// while a sterilize surgery is wanted, since the surgery needs a bed to lie in
// and an animal that is not hurt would otherwise never get one. Unknown care
// facts count nothing: the ward is topped up when they are read.
func WardBeds(rows []UpkeepAnimal, herd HerdPolicy) int {
	n := 0
	for _, a := range rows {
		if animalNeedsTending(a) {
			n++
		}
	}
	if n == 0 && len(sterilizeWanted(rows, herd)) > 0 {
		n = 1
	}
	return n
}

// animalNeedsTending is an animal with a condition no one has tended or that
// threatens its life.
func animalNeedsTending(a UpkeepAnimal) bool {
	if life, ok := a.LifeThreatening.Value(); ok && life {
		return true
	}
	conditions, ok := a.Conditions.Value()
	if !ok {
		return false
	}
	for _, c := range conditions {
		if tended, known := c.Tended.Value(); known && !tended {
			return true
		}
	}
	return false
}

// WardSlots are the animal bed slots of a hospital room, nearest the far wall
// first: floor outside the human template (the hospital's own beds and
// monitors) where an animal bed fits, the doorways stay reachable, and every
// bed keeps a free cell beside it to be tended from. Empty for a room the
// hospital template does not plan, or a bed whose shape is unread.
func WardSlots(room PlannedRoom, shapes PieceShapes, bed InteriorPieceDef) []InteriorPiece {
	if room.Role != PlannedHospital || bed.Def == "" || bed.Size.X < 1 || bed.Size.Z < 1 {
		return nil
	}
	in, ok := InteriorRoomFromLayout(room, shapes)
	if !ok {
		return nil
	}
	human, ok := PlanRoomInterior(in, InteriorPieceDef{})
	if !ok {
		return nil
	}
	blocked := map[domain.Cell]bool{}
	for _, p := range human.Pieces {
		for _, c := range rectCells(p.Rect) {
			blocked[c] = true
		}
	}
	// Candidates: every footprint in the interior, furthest from the doors
	// first, so the ward fills from the back and the entrance stays open.
	thresholds := interiorThresholds(in)
	dist := func(r Rectangle) int32 {
		best := int32(1 << 30)
		for _, t := range thresholds {
			d := abs32(r.X-t.X) + abs32(r.Z-t.Z)
			best = min(best, d)
		}
		return best
	}
	var cands []Rectangle
	for z := in.Interior.Z; z+bed.Size.Z <= in.Interior.Z+in.Interior.Height; z++ {
		for x := in.Interior.X; x+bed.Size.X <= in.Interior.X+in.Interior.Width; x++ {
			cands = append(cands, Rectangle{X: x, Z: z, Width: bed.Size.X, Height: bed.Size.Z})
		}
	}
	sort.SliceStable(cands, func(i, j int) bool {
		if di, dj := dist(cands[i]), dist(cands[j]); di != dj {
			return di > dj
		}
		return cellLess(domain.Cell{X: cands[i].X, Z: cands[i].Z}, domain.Cell{X: cands[j].X, Z: cands[j].Z})
	})
	var out []InteriorPiece
	for _, r := range cands {
		cells := rectCells(r)
		free := true
		for _, c := range cells {
			free = free && !blocked[c]
		}
		if !free || !InteriorPlacementWalkable(in, blocked, cells) || !wardBedsStayTended(in.Interior, blocked, out, cells) {
			continue
		}
		for _, c := range cells {
			blocked[c] = true
		}
		piece := NewInteriorPiece(fmt.Sprintf("ward.%d", len(out)+1), bed.Def, bed.Size, domain.North, domain.Cell{X: r.X, Z: r.Z})
		piece.Rect = r
		out = append(out, piece)
	}
	return out
}

// wardBedsStayTended reports that, with cells blocked too, every ward bed
// (those placed and the new one) still has a free floor cell beside it.
func wardBedsStayTended(interior Rectangle, blocked map[domain.Cell]bool, placed []InteriorPiece, cells []domain.Cell) bool {
	now := map[domain.Cell]bool{}
	for c := range blocked {
		now[c] = true
	}
	for _, c := range cells {
		now[c] = true
	}
	beds := [][]domain.Cell{cells}
	for _, p := range placed {
		beds = append(beds, rectCells(p.Rect))
	}
	for _, bed := range beds {
		free := false
		for _, c := range bed {
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				free = free || rectContains(interior, n) && !now[n]
			}
		}
		if !free {
			return false
		}
	}
	return true
}

// unflaggedWardBed is the first standing bed of def in room whose census row
// reads not medical; a bed the census lacks or whose flag is unread is not due
// yet.
func unflaggedWardBed(room PlannedRoom, built []CurrentBuilding, sleeping []SleepingBed, def string) (string, bool) {
	for _, b := range built {
		if b.Building.Definition() != def || len(b.Cells) == 0 || !rectInside(room.Region.Bounds(), cellsRectangle(b.Cells)) {
			continue
		}
		for _, s := range sleeping {
			if medical, known := s.Medical.Value(); s.ID == b.ID && known && !medical {
				return b.ID, true
			}
		}
	}
	return "", false
}

// WardReady is AnimalWard.Ready: known true when a hospital of the plan stands
// shelled (CensusRoomIn) with at least one standing animal bed whose census row
// reads medical. Known false when the plan has no hospital, none stands, or none
// of its standing beds is flagged. Unknown when a standing bed has no census row
// or an unread medical flag and no other bed proves the ward ready.
func WardReady(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, beds []SleepingBed, animalBed string) domain.Fact[bool] {
	unread := false
	for _, room := range plan.hospitals() {
		if _, standing := CensusRoomIn(room, rooms); !standing {
			continue
		}
		for _, b := range built {
			if b.Building.Definition() != animalBed || len(b.Cells) == 0 || !rectInside(room.Region.Bounds(), cellsRectangle(b.Cells)) {
				continue
			}
			read := false
			for _, s := range beds {
				if s.ID != b.ID {
					continue
				}
				medical, known := s.Medical.Value()
				if known && medical {
					return domain.Known(true)
				}
				read = read || known
			}
			unread = unread || !read
		}
	}
	if unread {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}

// wardTemplate is the hospital's animal ward as a reconcile template: the
// animal beds standing in room (up to quota) where they stand, the missing ones
// in the first free ward slots. absent reports a planned bed the room lacks.
func wardTemplate(room PlannedRoom, shapes PieceShapes, bed InteriorPieceDef, quota int, built []CurrentBuilding, taken map[domain.Cell]bool) (template []WantedPiece, absent bool) {
	var standing []WantedPiece
	for _, b := range built {
		if b.Building.Definition() == bed.Def && len(b.Cells) > 0 && rectInside(room.Region.Bounds(), cellsRectangle(b.Cells)) {
			r := cellsRectangle(b.Cells)
			standing = append(standing, WantedPiece{DefName: bed.Def, Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}})
		}
	}
	have := min(quota, len(standing))
	template = slices.Clone(standing[:have])
	for _, p := range WardSlots(room, shapes, bed) {
		if have >= quota {
			break
		}
		if slices.ContainsFunc(rectCells(p.Rect), func(c domain.Cell) bool { return taken[c] }) {
			continue
		}
		template = append(template, p.Wanted())
		absent = true
		have++
	}
	return template, absent
}
