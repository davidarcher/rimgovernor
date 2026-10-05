package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The freezer tomb (#840). A colonist kept frozen in a sarcophagus stays
// fresh for resurrector mech serum, so once the colony can build coolers a
// standing tomb that holds a buried colonist is cooled like the freezer:
// MaintainRefrigeration takes the room beside its food rooms, reuses or
// builds a cooler (the planned one over the tomb's exhaust reservation
// first) and sets it to FreezerTargetC. A tomb holding no colonist is left
// alone, and nothing waits on resurrector serum being in hand.

// TombMaxC is the warmest a tomb holding a colonist may measure before it
// owes cooling: corpses stop rotting at freezing.
const TombMaxC = 0.0

// WarmTombs lists the native room IDs of the standing planned tombs that
// hold a buried colonist, and of the standing meal closet (#936) and morgue (#1820), that
// measure warmer than TombMaxC, sorted. Known
// empty while coolers are unavailable; unknown while a fact it reads is.
func WarmTombs(shapes PieceShapes, coolers domain.Fact[bool], plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], waste domain.Fact[[]WasteItem], built domain.Fact[CurrentConstruction]) domain.Fact[[]string] {
	available, ak := coolers.Value()
	if ak && !available {
		return domain.Known[[]string](nil)
	}
	p, pk := plan.Value()
	r, rk := rooms.Value()
	w, wk := waste.Value()
	b, bk := built.Value()
	if !ak || !pk || !rk || !wk || !bk || !b.Colony {
		return domain.Unknown[[]string]()
	}
	filled := map[string]bool{}
	for _, item := range w {
		if item.CorpseOf == domain.CorpseColonist && item.State == WasteBuried {
			filled[item.Grave] = true
		}
	}
	var out []string
	for _, planned := range p.AllRooms() {
		if planned.Role != PlannedTomb && planned.Role != PlannedMorgue && planned.Role != PlannedMealCloset {
			continue
		}
		room, ok := CensusRoomIn(planned, r)
		if !ok {
			continue
		}
		// The standing meal closet (#936) is cooled like a filled tomb,
		// empty or not: the meal stockpile moves in once it stands. The
		// morgue (#1820) is shelled only for a waiting corpse, so it is
		// cooled the same way.
		occupied := planned.Role == PlannedMealCloset || planned.Role == PlannedMorgue
		inside := map[domain.Cell]bool{}
		for _, c := range room.Cells {
			inside[c] = true
		}
		for _, s := range b.Buildings {
			if s.Building.Definition() != shapes.Furniture.Sarcophagus || !filled[s.ID] || len(s.Cells) == 0 {
				continue
			}
			occupied = occupied || inside[s.Cells[0]]
		}
		if !occupied {
			continue
		}
		t, known := room.Temperature.Value()
		if !known {
			return domain.Unknown[[]string]()
		}
		if t > TombMaxC {
			out = append(out, room.ID)
		}
	}
	sort.Strings(out)
	return domain.Known(out)
}

// WithTombs adds the warm tombs to the review's rooms: any warm tomb makes
// it active. Unknown tombs leave the review as it is.
func (r RefrigerationReview) WithTombs(tombs domain.Fact[[]string]) RefrigerationReview {
	ids, known := tombs.Value()
	if !known || len(ids) == 0 {
		return r
	}
	seen := map[string]bool{}
	for _, id := range r.Rooms {
		seen[id] = true
	}
	for _, id := range ids {
		if !seen[id] {
			r.Rooms = append(r.Rooms, id)
		}
	}
	sort.Strings(r.Rooms)
	r.Active, r.Tombs = true, ids
	return r
}
