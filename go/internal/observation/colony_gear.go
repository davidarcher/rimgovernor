package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func colonyGear(v *o.ColonyFactsSnapshot) domain.Fact[policy.GearObservation] {
	gear := v.GetPlanning().GetObserved().GetGear()
	if gear == nil || v.ColonistCount == nil || uint32(len(gear.Pawns)) != v.GetColonistCount() {
		return domain.Unknown[policy.GearObservation]()
	}
	result := policy.GearObservation{Pawns: []policy.GearPawn{}}
	for _, p := range gear.Pawns {
		row := policy.GearPawn{Pawn: policy.PawnID(p.Pawn.GetId()), Loadout: p.Snapshot.GetToken(), Blocked: p.Blocker != nil, Deficit: optional(p.Deficit)}
		candidates := []policy.GearCandidate{}
		for _, c := range p.Candidates {
			candidates = append(candidates, policy.GearCandidate{Target: c.Item.Thing.GetId(), Gain: c.GetGain(), Definition: policy.Resource(c.Item.Thing.GetDefName())})
		}
		needs := []policy.GearReplacement{}
		for _, n := range p.ReplacementNeeds {
			needs = append(needs, policy.GearReplacement{Definition: policy.Resource(n.GetDefName()), Stuff: policy.Resource(n.GetStuff()), Reason: n.GetReason()})
		}
		row.Candidates = domain.Known(candidates)
		row.Replacements = domain.Known(needs)
		result.Pawns = append(result.Pawns, row)
	}
	return domain.Known(result)
}

func routineGear(v domain.Fact[policy.GearObservation], emergency policy.EmergencyFacts) domain.Fact[policy.GearObservation] {
	gear, known := v.Value()
	complete, ck := emergency.ColonistsComplete.Value()
	if !known || !ck || !complete || len(gear.Pawns) != len(emergency.Colonists) {
		return domain.Unknown[policy.GearObservation]()
	}
	ids := map[policy.PawnID]bool{}
	for _, p := range emergency.Colonists {
		if ids[p.ID] {
			return domain.Unknown[policy.GearObservation]()
		}
		ids[p.ID] = true
	}
	for _, p := range gear.Pawns {
		if !ids[p.Pawn] {
			return domain.Unknown[policy.GearObservation]()
		}
		delete(ids, p.Pawn)
	}
	return v
}
