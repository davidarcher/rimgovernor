package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Room quality gap closer (#814, B3/B4 of #799): an owned bedroom (or a
// dining or rec room, #816, against CommonRoomTargets) below its
// RoomTarget.Min gets one furniture piece at a time from the #802 bedroom
// template's optional slots. The weakest of wealth, beauty, space and
// cleanliness decides whether a piece helps at all:
//   - space weakest: nothing is added, since every furniture tile costs the
//     room 0.9 space and the weakest stat carries about half the score;
//     the owner is given a suite instead (SuiteClaims, #1216);
//   - otherwise the slots go in lever order (end table, dresser, then the
//     lamp), each adding wealth.
//
// Flooring is not a lever here: MaintainFlooring already floors every
// bedroom at the living tier. A room marked NeverUpgrade, or at or above
// its Max, is never touched.

// RoomStat names one input of the native Impressiveness stat.
type RoomStat string

const (
	RoomStatWealth      RoomStat = "wealth"
	RoomStatBeauty      RoomStat = "beauty"
	RoomStatSpace       RoomStat = "space"
	RoomStatCleanliness RoomStat = "cleanliness"
)

// WeakestRoomStat divides each stat by the native normaliser
// RoomStatWorker_Impressiveness uses before combining (wealth 1500,
// beauty 3, space 125, cleanliness 2.5 after the +2.5 shift), so the weakest stat is compared on
// the game's own footing.
func WeakestRoomStat(q RoomQuality) RoomStat {
	scores := []struct {
		stat RoomStat
		v    float64
	}{
		{RoomStatWealth, q.Wealth / 1500},
		{RoomStatBeauty, q.Beauty / 3},
		{RoomStatSpace, q.Space / 125},
		{RoomStatCleanliness, (q.Cleanliness + 2.5) / 2.5},
	}
	best := scores[0]
	for _, s := range scores[1:] {
		if s.v < best.v {
			best = s
		}
	}
	return best.stat
}

// roomUpgradeSlots are the bedroom template slots the closer fills, cheapest
// lever first.
var roomUpgradeSlots = []string{"end_table", "dresser", "lamp"}

// RoomBeautyDefinitions are the definitions the closer may place beside the
// furniture rules' (RoomFurniture.Definitions, read with every catalog): the
// standing lamp, the plant pot and the floors, the beauty levers (#830). The
// planning census must read them for availability and stuff.
func RoomBeautyDefinitions() []string {
	return append([]string{standingLampDef, PlantPotDefinition}, DefaultFlooringPolicy().Floors...)
}

// RoomUpgrade is one PlaceBuilding the closer wants.
type RoomUpgrade struct {
	Room    string
	Slot    string
	Def     string
	Anchor  domain.Cell
	Rot     domain.Rotation
	Weakest RoomStat
	// Cells, when set, places Def at each cell (a floor, #830).
	Cells []domain.Cell
	// Stuff, when set, overrides the definition's default stuff (#842).
	Stuff string
}

// NextRoomUpgrade returns the first (by room id) upgrade due, false when
// none. rooms are the furniture rooms (TidyFurnitureRooms); available
// reports a definition the colony can build now.
func NextRoomUpgrade(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, available func(string) bool, gate RoomGate) (RoomUpgrade, bool) {
	census, ok := obs.Rooms.Value()
	if !ok {
		return RoomUpgrade{}, false
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
	ids := make([]string, 0, len(targets))
	for id := range targets {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		t := targets[id]
		q, qk := quality[id]
		room, rk := furniture[id]
		if !qk || !rk || t.NeverUpgrade || t.Min <= 0 || q.Impressiveness >= t.Min || (t.Max > 0 && q.Impressiveness >= t.Max) {
			continue
		}
		weakest := WeakestRoomStat(q)
		if weakest == RoomStatSpace {
			continue
		}
		plan, ok := PlanInterior(room.Room, InteriorPieceDef{})
		if !ok {
			continue
		}
		have := map[string]bool{}
		for _, p := range room.Pieces {
			have[p.Def] = true
		}
		for _, slot := range roomUpgradeSlots {
			for _, piece := range plan.Pieces {
				if piece.Slot != slot || have[piece.Def] || !available(piece.Def) || roomPieceOverlaps(room.Pieces, piece.Rect) || !gate.PieceAllowed(t.Owners, piece.Def) {
					continue
				}
				return RoomUpgrade{Room: id, Slot: slot, Def: piece.Def, Anchor: piece.Anchor(), Rot: piece.Rot, Weakest: weakest}, true
			}
		}
	}
	return RoomUpgrade{}, false
}

func roomPieceOverlaps(pieces []TidyPiece, r Rectangle) bool {
	for _, p := range pieces {
		a := p.Rect
		if a.X < r.X+r.Width && r.X < a.X+a.Width && a.Z < r.Z+r.Height && r.Z < a.Z+a.Height {
			return true
		}
	}
	return false
}
