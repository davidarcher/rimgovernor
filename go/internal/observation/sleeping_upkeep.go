package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonySleeping(v *o.ColonyFactsSnapshot) domain.Fact[policy.SleepingObservation] {
	u := v.GetUpkeep().GetObserved()
	if u == nil || v.ColonistCount == nil || hasIssue(u.Issues, "people") || hasIssue(u.Issues, "beds") {
		return domain.Unknown[policy.SleepingObservation]()
	}
	r := policy.SleepingObservation{Colonists: int(v.GetColonistCount())}
	ids := func(values []string) []policy.PawnID {
		rows := []policy.PawnID{}
		for _, v := range values {
			rows = append(rows, policy.PawnID(v))
		}
		return rows
	}
	person := func(p *o.UpkeepPerson) policy.SleepingPerson {
		return policy.SleepingPerson{ID: policy.PawnID(p.Pawn.Pawn.GetId()), OwnedBed: optional(p.OwnedBedId), ComfortableMin: optional(p.ComfortableMinC), ComfortableMax: optional(p.ComfortableMaxC), Partners: ids(p.PartnerIds), BedSharingAllowed: optional(p.BedSharingAllowed), Title: royalTitle(p.Title)}
	}
	for _, p := range u.People {
		r.People = append(r.People, person(p))
	}
	for _, p := range u.Slaves {
		r.Slaves = append(r.Slaves, person(p))
	}
	for _, b := range u.Beds {
		r.Beds = append(r.Beds, policy.SleepingBed{ID: b.Bed.GetId(), Definition: policy.Resource(b.Bed.GetDefName()), Humanlike: optional(b.Humanlike), Medical: optional(b.Medical), Prisoners: optional(b.Prisoners), Slaves: b.GetForSlaves(), Roofed: optional(b.Roofed), RestEffectiveness: optional(b.RestEffectiveness), Temperature: optional(b.TemperatureC), Owners: ids(b.Owners), Users: ids(b.Users), AccessibleTo: ids(b.AccessibleTo), Room: optional(b.RoomId), Quality: optional(b.Quality), Stuff: optional(b.Stuff), Cell: domain.Cell{X: b.Bed.GetPosition().GetX(), Z: b.Bed.GetPosition().GetZ()}})
	}
	return domain.Known(r)
}

// upkeepRooms is the room-quality view of the frame's rooms census (#1338):
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

func royalTitle(t *o.RoyalTitleFacts) *policy.RoyalTitle {
	if t == nil {
		return nil
	}
	r := &policy.RoyalTitle{Definition: t.GetDefName(), Seniority: int(t.GetSeniority()), BedroomMinArea: int(t.GetBedroomMinArea()), BedroomMinImpressiveness: int(t.GetBedroomMinImpressiveness()), BedroomFloored: t.GetBedroomFloored()}
	for _, req := range t.BedroomThings {
		thing := policy.BedroomThing{Count: int(req.GetCount())}
		for _, def := range req.AnyOf {
			thing.AnyOf = append(thing.AnyOf, policy.Resource(def))
		}
		r.BedroomThings = append(r.BedroomThings, thing)
	}
	return r
}
