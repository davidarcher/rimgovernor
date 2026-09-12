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
		r.People = append(r.People, policy.SleepingPerson{ID: policy.PawnID(p.Pawn.Pawn.GetId()), OwnedBed: optional(p.OwnedBedId), ComfortableMin: optional(p.ComfortableMinC), ComfortableMax: optional(p.ComfortableMaxC)})
	}
	for _, b := range u.Beds {
		r.Beds = append(r.Beds, policy.SleepingBed{ID: b.Bed.GetId(), Definition: policy.Resource(b.Bed.GetDefName()), Humanlike: optional(b.Humanlike), Medical: optional(b.Medical), Prisoners: optional(b.Prisoners), Roofed: optional(b.Roofed), RestEffectiveness: optional(b.RestEffectiveness), Temperature: optional(b.TemperatureC), Owners: ids(b.Owners), Users: ids(b.Users), AccessibleTo: ids(b.AccessibleTo)})
	}
	return domain.Known(r)
}
