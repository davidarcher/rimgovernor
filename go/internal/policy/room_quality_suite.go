package policy

import (
	"math"
	"sort"
)

// Suites for qualifying pawns (#1216, epic #1200). Where the gap closer
// and the bed ladder skip a room whose weakest stat is space, the pawn is
// given a suite instead: the plan grows the suite wing by one suite sized
// for the pawn's target (SuiteTargets into Grow), the bedroom ladder
// shells and furnishes it and moves the pawn in (NextBedroomStep), and
// the standard room left behind is vacant for the next unhoused pawn. No
// claim is stored: a pawn qualifies while it holds a standard room its
// target outgrows, and a planned suite nobody owns is the next claimant's.

// SuiteClaim is one pawn a suite is wanted for: the owner of Bed, a solo
// standard bedroom that cannot reach Target.
type SuiteClaim struct {
	Pawn   PawnID
	Bed    string
	Target float64
}

// suiteCells is the interior area whose space alone meets target (see
// SuiteSize), before snapping.
func suiteCells(target float64) int32 {
	space := 125 * max(target, 0) / 100
	return int32(math.Ceil((space + 0.9*suiteFurnitureTiles) / 1.4))
}

// plannedRoomIDs maps the census id of each standing planned room of role
// to its plan room.
func plannedRoomIDs(plan LayoutPlan, rooms RoomObservation, role ModuleRole) map[string]LayoutRoom {
	out := map[string]LayoutRoom{}
	for _, r := range plan.AllRooms() {
		if r.Role != role {
			continue
		}
		standing := PlannedRoomStanding
		if role == ModuleSuite {
			standing = suiteHome
		}
		if room, ok := standing(r, rooms); ok {
			out[room.ID] = r
		}
	}
	return out
}

// suiteHome is the census room a planned suite is lived in: the suite as
// planned, else, while it grows (#1218), the enclosed room inside its
// planned interior that holds a bed.
func suiteHome(r LayoutRoom, rooms RoomObservation) (Room, bool) {
	if room, ok := PlannedRoomStanding(r, rooms); ok {
		return room, true
	}
	for _, room := range roomsInside(r.Interior, rooms) {
		if len(room.Beds) > 0 {
			return room, true
		}
	}
	return Room{}, false
}

// roomsInside is the enclosed census rooms whose floor lies wholly inside
// in.
func roomsInside(in Rectangle, rooms RoomObservation) []Room {
	var out []Room
	for _, room := range rooms.Rooms {
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed || len(room.Cells) == 0 {
			continue
		}
		inside := true
		for _, c := range room.Cells {
			inside = inside && c.X >= in.X && c.Z >= in.Z && c.X < in.X+in.Width && c.Z < in.Z+in.Height
		}
		if inside {
			out = append(out, room)
		}
	}
	return out
}

// SuiteRoomIDs is the census ids of the plan's standing suites.
func SuiteRoomIDs(plan LayoutPlan, rooms RoomObservation) map[string]bool {
	out := map[string]bool{}
	for id := range plannedRoomIDs(plan, rooms, ModuleSuite) {
		out[id] = true
	}
	return out
}

// SuiteClaims is every pawn owed a suite, most suite pressure first (#1217): the sole owner of a
// standing planned standard bedroom below its target's Min whose space is
// the weakest stat, or whose target needs more floor than the room has
// (Greedy, Jealous of a suite, a title), and the owner of a suite that
// cannot grow outward to its target (#1218). An ascetic never gets one.
func SuiteClaims(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget, traits map[PawnID]TraitEffects, pressure map[PawnID]float64) []SuiteClaim {
	census, ok := sleeping.Rooms.Value()
	if !ok {
		return nil
	}
	quality := map[string]RoomQuality{}
	for _, r := range census {
		if q, ok := r.Quality.Value(); ok {
			quality[r.ID] = q
		}
	}
	standard := plannedRoomIDs(plan, rooms, ModuleBedroom)
	suites := plannedRoomIDs(plan, rooms, ModuleSuite)
	var out []SuiteClaim
	for _, s := range soloBedrooms(sleeping) {
		t, tk := targets[s.room]
		q, qk := quality[s.room]
		if !tk || !qk || traits[s.owner].Ascetic || t.NeverUpgrade || t.Min <= 0 || q.Impressiveness >= t.Min {
			continue
		}
		// A suite that cannot grow outward to its owner's target (#1218)
		// is left for a larger new one.
		if r, ok := suites[s.room]; ok {
			area := r.Interior.Width * r.Interior.Height
			w, d := SuiteSize(t.Min)
			if suiteCells(t.Min) <= area || w*d <= area {
				continue
			}
			if wing, ok := suiteWingRoom(plan, r); ok {
				if _, grows := grownSuite(plan, wing, r, t.Min); grows {
					continue
				}
			}
			out = append(out, SuiteClaim{Pawn: s.owner, Bed: s.bed, Target: t.Min})
			continue
		}
		r, planned := standard[s.room]
		if !planned {
			continue
		}
		if WeakestRoomStat(q) != RoomStatSpace && suiteCells(t.Min) <= r.Interior.Width*r.Interior.Height {
			continue
		}
		out = append(out, SuiteClaim{Pawn: s.owner, Bed: s.bed, Target: t.Min})
	}
	orderSuiteClaims(out, pressure)
	return out
}

// vacantSuites is the plan's suites nobody owns a bed in, in plan order:
// unbuilt, standing empty, or holding only unowned beds.
func vacantSuites(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation) []LayoutRoom {
	owned := map[string]bool{}
	for _, b := range sleeping.Beds {
		if len(b.Owners) > 0 {
			owned[b.ID] = true
		}
	}
	var out []LayoutRoom
	for _, r := range plan.AllRooms() {
		if r.Role != ModuleSuite {
			continue
		}
		room, ok := suiteHome(r, rooms)
		taken := false
		for _, b := range room.Beds {
			taken = taken || ok && owned[b]
		}
		if !taken {
			out = append(out, r)
		}
	}
	return out
}

// SuiteRooms is how many suites the plan holds.
func (p LayoutPlan) SuiteRooms() int {
	n := 0
	for _, r := range p.AllRooms() {
		if r.Role == ModuleSuite {
			n++
		}
	}
	return n
}

// SuiteTargets is Grow's suites argument: one entry per suite the plan
// holds, its sole owner's target while the suite is below it (Grow grows
// it outward, #1218) and zero otherwise (kept as it is), then the target
// of each claim no vacant suite answers.
func SuiteTargets(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget, claims []SuiteClaim) []float64 {
	out := make([]float64, plan.SuiteRooms())
	below := map[string]float64{}
	if census, ok := sleeping.Rooms.Value(); ok {
		for _, r := range census {
			q, qk := r.Quality.Value()
			t, tk := targets[r.ID]
			if qk && tk && !t.NeverUpgrade && q.Impressiveness < t.Min {
				below[r.ID] = t.Min
			}
		}
	}
	solo := map[string]bool{}
	for _, s := range soloBedrooms(sleeping) {
		solo[s.room] = true
	}
	if i := wingOf(plan.Wings, WingSuites); i >= 0 {
		for k, r := range plan.Wings[i].Rooms {
			if room, ok := suiteHome(r, rooms); ok && k < len(out) && solo[room.ID] {
				out[k] = below[room.ID]
			}
		}
	}
	for _, c := range claims[min(len(vacantSuites(plan, rooms, sleeping)), len(claims)):] {
		out = append(out, c.Target)
	}
	return out
}

// nextSuiteStep walks the first claim into its suite: the claims take the
// vacant suites in order, and the first whose suite needs anything gets
// its shell, then a bed, then the move.
func nextSuiteStep(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, claims []SuiteClaim) BedroomStep {
	beds := map[string]SleepingBed{}
	for _, b := range sleeping.Beds {
		beds[b.ID] = b
	}
	vacant := vacantSuites(plan, rooms, sleeping)
	for i, c := range claims {
		if i >= len(vacant) {
			break
		}
		r := vacant[i]
		room, ok := PlannedRoomStanding(r, rooms)
		if !ok {
			return BedroomStep{Kind: BedroomShell, Room: r}
		}
		if len(room.Beds) == 0 {
			return BedroomStep{Kind: BedroomFurnish, Room: r, Cells: room.Cells}
		}
		ids := append([]string(nil), room.Beds...)
		sort.Strings(ids)
		for _, id := range ids {
			if b, ok := beds[id]; ok && containsPawn(b.AccessibleTo, c.Pawn) {
				return BedroomStep{Kind: BedroomMove, Pawn: c.Pawn, Bed: id, PreviousBed: c.Bed, Room: r}
			}
		}
	}
	return BedroomStep{}
}
