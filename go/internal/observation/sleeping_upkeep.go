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
	for _, p := range u.People {
		r.People = append(r.People, policy.SleepingPerson{ID: policy.PawnID(p.Pawn.Pawn.GetId()), OwnedBed: optional(p.OwnedBedId), ComfortableMin: optional(p.ComfortableMinC), ComfortableMax: optional(p.ComfortableMaxC), Partners: ids(p.PartnerIds), BedSharingAllowed: optional(p.BedSharingAllowed), Title: royalTitle(p.Title)})
	}
	for _, b := range u.Beds {
		r.Beds = append(r.Beds, policy.SleepingBed{ID: b.Bed.GetId(), Definition: policy.Resource(b.Bed.GetDefName()), Humanlike: optional(b.Humanlike), Medical: optional(b.Medical), Prisoners: optional(b.Prisoners), Roofed: optional(b.Roofed), RestEffectiveness: optional(b.RestEffectiveness), Temperature: optional(b.TemperatureC), Owners: ids(b.Owners), Users: ids(b.Users), AccessibleTo: ids(b.AccessibleTo), Room: optional(b.RoomId), Quality: optional(b.Quality)})
	}
	if !hasIssue(u.Issues, "rooms") {
		r.Rooms = upkeepRooms(u.Rooms)
	}
	return domain.Known(r)
}

// upkeepRooms decodes the upkeep room census; a missing or unavailable
// section is unknown, and a row missing any quality stat has unknown quality.
func upkeepRooms(section *o.UpkeepRoomsSection) domain.Fact[[]policy.UpkeepRoom] {
	f := section.GetObserved()
	if f == nil {
		return domain.Unknown[[]policy.UpkeepRoom]()
	}
	rows := []policy.UpkeepRoom{}
	for _, r := range f.Rooms {
		cells := domain.Unknown[int]()
		if r.CellCount != nil {
			cells = domain.Known(int(r.GetCellCount()))
		}
		quality := domain.Unknown[policy.RoomQuality]()
		if q := r.Quality; q != nil && q.Space != nil && q.Beauty != nil && q.Cleanliness != nil && q.Wealth != nil && q.Impressiveness != nil {
			quality = domain.Known(policy.RoomQuality{Space: q.GetSpace(), Beauty: q.GetBeauty(), Cleanliness: q.GetCleanliness(), Wealth: q.GetWealth(), Impressiveness: q.GetImpressiveness()})
		}
		rows = append(rows, policy.UpkeepRoom{ID: r.GetRoomId(), Role: r.GetRole(), Quality: quality, Cells: cells, Beds: append([]string{}, r.BedIds...)})
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
