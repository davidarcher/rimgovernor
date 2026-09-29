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
		if room, ok := PlannedRoomStanding(r, rooms); ok {
			out[room.ID] = r
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
// (Greedy, Jealous of a suite, a title). An ascetic never gets one.
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
	var out []SuiteClaim
	for _, s := range soloBedrooms(sleeping) {
		r, planned := standard[s.room]
		t, tk := targets[s.room]
		q, qk := quality[s.room]
		if !planned || !tk || !qk || traits[s.owner].Ascetic || t.NeverUpgrade || t.Min <= 0 || q.Impressiveness >= t.Min {
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
		room, ok := PlannedRoomStanding(r, rooms)
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
// holds (kept as they are), then the target of each claim no vacant suite
// answers.
func SuiteTargets(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, claims []SuiteClaim) []float64 {
	out := make([]float64, plan.SuiteRooms())
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
