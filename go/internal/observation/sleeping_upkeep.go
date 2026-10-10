package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonySleeping(v *o.ColonyFactsSnapshot, buildings bridge.Buildings, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.SleepingObservation], error) {
	u := v.GetUpkeep().GetObserved()
	if u == nil || v.ColonistCount == nil || hasIssue(u.Issues, "people") || hasIssue(u.Issues, "beds") {
		return domain.Unknown[policy.SleepingObservation](), nil
	}
	r := policy.SleepingObservation{Colonists: int(v.GetColonistCount())}
	ids := func(values []string) []policy.PawnID {
		rows := []policy.PawnID{}
		for _, v := range values {
			rows = append(rows, policy.PawnID(v))
		}
		return rows
	}
	var err error
	people := func(rows []*o.UpkeepPerson) []policy.SleepingPerson {
		var out []policy.SleepingPerson
		for _, p := range rows {
			var title *policy.RoyalTitle
			if err == nil && p.RoyalTitle != nil {
				title, err = catalog.RoyalTitleOf(p.GetRoyalTitle(), p.GetAscetic(), p.Precepts)
			}
			out = append(out, policy.SleepingPerson{ID: policy.PawnID(p.Pawn.GetId()), OwnedBed: domain.Known(p.GetOwnedBed().GetId()), ComfortableMin: optional(p.ComfortableMinC), ComfortableMax: optional(p.ComfortableMaxC), Partners: ids(bridge.RefIDs(p.Partners)), BedSharingAllowed: optional(p.BedSharingAllowed), Title: title})
		}
		return out
	}
	r.People, r.Slaves, r.Guests = people(u.People), people(u.Slaves), people(u.Guests)
	if err != nil {
		return domain.Unknown[policy.SleepingObservation](), err
	}
	for _, b := range u.Beds {
		head := buildings.Entity(b.Bed)
		if head == nil {
			return domain.Unknown[policy.SleepingObservation](), nil
		}
		r.Beds = append(r.Beds, policy.SleepingBed{ID: b.Bed.GetId(), Definition: policy.Resource(head.GetDefName()), Humanlike: optional(b.Humanlike), Medical: optional(b.Medical), Prisoners: optional(b.Prisoners), Slaves: b.GetForSlaves(), Roofed: optional(b.Roofed), RestEffectiveness: optional(b.RestEffectiveness), Temperature: optional(b.TemperatureC), Owners: ids(bridge.RefIDs(b.Owners)), Users: ids(bridge.RefIDs(b.Users)), AccessibleTo: ids(bridge.RefIDs(b.AccessibleTo)), Room: optionalRef(b.Room), Quality: optional(b.Quality), Stuff: optional(b.Stuff), Cell: domain.Cell{X: head.GetPosition().GetX(), Z: head.GetPosition().GetZ()}})
	}
	return domain.Known(r), nil
}

// upkeepRooms is the room-quality view of the frame's rooms census:
// every room holding a colonist bed (humanlike, not medical, not for
// prisoners) or carrying a common role (dining, rec room) and not fogged,
// with its native stats. Unknown when a bed's kind or room is unknown; a
// room missing any quality stat has unknown quality.
func upkeepRooms(census *o.RoomsSnapshot, beds []policy.SleepingBed) domain.Fact[[]policy.UpkeepRoom] {
	owned := map[string][]string{}
	for _, b := range beds {
		human, hk := b.Humanlike.Value()
		medical, mk := b.Medical.Value()
		prisoners, pk := b.Prisoners.Value()
		if !hk || !mk || !pk {
			return domain.Unknown[[]policy.UpkeepRoom]()
		}
		if !human || medical || prisoners {
			continue
		}
		room, known := b.Room.Value()
		if !known {
			return domain.Unknown[[]policy.UpkeepRoom]()
		}
		owned[room] = append(owned[room], b.ID)
	}
	rows := []policy.UpkeepRoom{}
	for _, r := range census.GetRooms() {
		role := r.GetRole()
		if len(owned[r.GetId()]) == 0 && (r.GetFogged() || role != "DiningRoom" && role != "RecRoom") {
			continue
		}
		cells := domain.Unknown[int]()
		if r.CellCount != nil {
			cells = domain.Known(int(r.GetCellCount()))
		}
		quality := domain.Unknown[policy.RoomQuality]()
		space, beauty, clean, wealth, impressive := roomStat(r, "Space"), roomStat(r, "Beauty"), roomStat(r, "Cleanliness"), roomStat(r, "Wealth"), roomStat(r, "Impressiveness")
		s, sk := space.Value()
		b, bk := beauty.Value()
		c, ck := clean.Value()
		w, wk := wealth.Value()
		i, ik := impressive.Value()
		if sk && bk && ck && wk && ik {
			quality = domain.Known(policy.RoomQuality{Space: s, Beauty: b, Cleanliness: c, Wealth: w, Impressiveness: i})
		}
		rows = append(rows, policy.UpkeepRoom{ID: r.GetId(), Role: role, Quality: quality, Cells: cells, Beds: append([]string{}, owned[r.GetId()]...)})
	}
	return domain.Known(rows)
}
