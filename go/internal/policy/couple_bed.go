package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Couple beds (#843). When two colonists become a couple (sleepingCouples)
// and share no double bed, the couple's room gets one in its bedroom
// template's bed slot. The change is walked across reviews, each step read
// back from the census:
//   - pack: the single beds the two own (the couple's room's first) are
//     uninstalled; vanilla hauling stores the packed items, and the
//     uninstall frees the partner's old room;
//   - install: the couple's room, found from the cell of its packed bed,
//     gets a DoubleBed in the template's bed slot: a stored one reinstalled,
//     else a new one built.
//
// The ordinary assignment then gives both partners the double bed
// (ReviewSleeping offers a couple's double bed first).

// PackedFurnitureDefinition is the packed (minified) item a bed becomes.
const PackedFurnitureDefinition = "MinifiedThing"

type CoupleBedKind string

const (
	CouplePack    CoupleBedKind = "pack"
	CoupleInstall CoupleBedKind = "install"
)

// CoupleBed is the next couple bed step. Pack lists the beds to uninstall
// (the couple's room's first); Room is the room's census id. An install is the
// build side's (#2115): Planned is the couple's planned room and Template its
// DoubleBed in the bedroom template's bed slot, which ReconcileRoom installs
// from stock or builds.
type CoupleBed struct {
	Kind          CoupleBedKind
	Pawn, Partner PawnID
	Room          string
	Pack          []TidyPiece
	Planned       PlannedRoom
	Template      []WantedPiece
}

// coupleBedSlot is the bedroom template's bed slot for a DoubleBed in room,
// and the planned room of plan that room stands in; false when plan has none.
func coupleBedSlot(plan LayoutPlan, room TidyRoom) (PlannedRoom, []WantedPiece, bool) {
	for _, planned := range plan.AllRooms() {
		if planned.Interior != room.Room.Interior {
			continue
		}
		template, ok := BedroomTemplate(planned, room.Room.Shapes, SleepingCoupleBedDefinition)
		return planned, template, ok
	}
	return PlannedRoom{}, nil, false
}

// NextCoupleBed returns the first couple (by lower pawn id) bed step due,
// false when none. rooms are the furniture rooms, each of which must stand in a
// planned room of plan (its install is reconciled, #2115); packed are the cells
// of the beds this Episode's completed pack steps uninstalled, the
// couple's room's bed first; buildable reports that a DoubleBed can be
// built, without which nothing is packed.
func NextCoupleBed(obs SleepingObservation, plan LayoutPlan, rooms []TidyRoom, packed []domain.Cell, buildable bool) (CoupleBed, bool) {
	couples := sleepingCouples(obs.People)
	owned := map[PawnID]string{}
	for _, p := range obs.People {
		owned[p.ID], _ = p.OwnedBed.Value()
	}
	beds := map[string]SleepingBed{}
	for _, b := range obs.Beds {
		beds[b.ID] = b
	}
	pieceOf := func(bed string) (TidyPiece, TidyRoom, bool) {
		for _, r := range rooms {
			for _, p := range r.Pieces {
				if p.Thing == bed {
					return p, r, true
				}
			}
		}
		return TidyPiece{}, TidyRoom{}, false
	}
	var pawns []PawnID
	for p, q := range couples {
		if p < q {
			pawns = append(pawns, p)
		}
	}
	sort.Slice(pawns, func(i, j int) bool { return pawns[i] < pawns[j] })
	for _, p := range pawns {
		q := couples[p]
		// A double bed only the couple holds (or nobody) is the ordinary
		// assignment's to give.
		shared := false
		for _, b := range obs.Beds {
			only := true
			for _, o := range b.Owners {
				only = only && (o == p || o == q)
			}
			humanlike, _ := b.Humanlike.Value()
			shared = shared || SleepingDoubleBeds[b.Definition] && humanlike && only
		}
		if shared {
			continue
		}
		step := CoupleBed{Pawn: p, Partner: q}
		var home TidyRoom
		for _, pawn := range []PawnID{p, q} {
			b, ok := beds[owned[pawn]]
			if !ok || SleepingDoubleBeds[b.Definition] {
				continue
			}
			only := true
			for _, o := range b.Owners {
				only = only && (o == p || o == q)
			}
			piece, room, found := pieceOf(b.ID)
			if !only || !found {
				continue
			}
			if step.Room == "" {
				step.Room, home = room.ID, room
			}
			if len(step.Pack) == 0 || step.Pack[0].Thing != piece.Thing {
				step.Pack = append(step.Pack, piece)
			}
		}
		if len(step.Pack) > 0 {
			if _, _, ok := coupleBedSlot(plan, home); !ok || !buildable {
				continue
			}
			step.Kind = CouplePack
			return step, true
		}
		if len(packed) == 0 {
			continue
		}
		for _, r := range rooms {
			if cellInRect(r.Room.Interior, packed[0]) {
				planned, template, ok := coupleBedSlot(plan, r)
				if !ok {
					break
				}
				return CoupleBed{Kind: CoupleInstall, Pawn: p, Partner: q, Room: r.ID, Planned: planned, Template: template}, true
			}
		}
	}
	return CoupleBed{}, false
}

// BedSpot is the first free anchor in room where a bed of def fits (the
// bed replacement's placement), for reinstalling a stored bed.
func BedSpot(room TidyRoom, def Resource) (domain.Cell, domain.Rotation, bool) {
	return bedSpot(room, def)
}

// CoupleBedRooms is TidyFurnitureRooms for the couple bed lever. Packing
// the couple's beds leaves their room bedless, and native then reads it as
// RoomRoleNone, which has no interior template; such an empty room is read
// as a bedroom so the install step still finds it by its packed cell.
func CoupleBedRooms(rooms RoomObservation, census CurrentConstruction, cells []SiteCell) []TidyRoom {
	read := rooms
	read.Rooms = make([]Room, len(rooms.Rooms))
	for i, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleNone && len(room.Beds) == 0 {
			room.Role = domain.Known(RoomRoleBedroom)
		}
		read.Rooms[i] = room
	}
	return TidyFurnitureRooms(read, census, cells)
}

func cellInRect(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}
