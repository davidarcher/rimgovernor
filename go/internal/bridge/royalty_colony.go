package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateRoyaltyColony checks the Royalty colony section: every row
// decodes (unique, well-formed ids, nonnegative counts). Absent scalars stay
// unknown.
func validateRoyaltyColony(v *o.ColonyFactsSnapshot) error {
	if v.Royalty == nil {
		return nil
	}
	switch s := v.Royalty.Outcome.(type) {
	case *o.RoyaltySection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.RoyaltySection_Observed:
		_, err := DecodeRoyaltyColony(s.Observed)
		return err
	default:
		return contract("missing royalty colony outcome")
	}
}

// DecodeRoyaltyColony validates the Royalty colony section's facts: the
// neuroformer stock, pending bestowing ceremonies and standing thrones.
func DecodeRoyaltyColony(v *o.RoyaltyColonyFacts) (policy.RoyaltyColony, error) {
	if v == nil {
		return policy.RoyaltyColony{}, contract("incomplete royalty colony facts")
	}
	out := policy.RoyaltyColony{Neuroformers: map[string]policy.Neuroformer{}}
	for _, row := range v.Neuroformers {
		name := row.GetDefName()
		if validID(name) != nil || row.TeachesPsycast != nil && validID(row.GetTeachesPsycast()) != nil || row.GetHeld() < 0 {
			return policy.RoyaltyColony{}, contract("invalid royalty neuroformer")
		}
		if _, exists := out.Neuroformers[name]; exists {
			return policy.RoyaltyColony{}, contract("duplicate royalty neuroformer %s", name)
		}
		out.Neuroformers[name] = policy.Neuroformer{Def: name, TeachesPsycast: row.GetTeachesPsycast(), Held: optionalFact(intPtr(row.Held)),
			Craftable: optionalFact(row.Craftable), Tradeable: optionalFact(row.Tradeable)}
	}
	seen := map[string]bool{}
	for _, row := range v.Ceremonies {
		pawn, bestower := row.GetPawn().GetId(), row.GetBestower().GetId()
		if validID(pawn) != nil || bestower != "" && validID(bestower) != nil || validID(row.GetQuest()) != nil || row.FactionDef != nil && validID(row.GetFactionDef()) != nil ||
			row.Title != nil && validID(row.GetTitle()) != nil || seen[pawn+"/"+row.GetFactionDef()] {
			return policy.RoyaltyColony{}, contract("invalid royalty ceremony")
		}
		seen[pawn+"/"+row.GetFactionDef()] = true
		ceremony := policy.BestowingCeremony{Quest: domain.QuestID(row.GetQuest()), Pawn: policy.PawnID(pawn), Bestower: policy.PawnID(bestower), Faction: row.GetFactionDef(), Title: row.GetTitle(),
			Accepted: optionalFact(row.Accepted), BestowerWaiting: optionalFact(row.BestowerWaiting), Started: optionalFact(row.Started)}
		if row.Spot != nil {
			ceremony.Spot = domain.Known(domain.Cell{X: row.Spot.GetX(), Z: row.Spot.GetZ()})
		}
		for _, a := range row.Attendees {
			if validID(a.GetId()) != nil {
				return policy.RoyaltyColony{}, contract("invalid royalty ceremony attendee")
			}
			ceremony.Attendees = append(ceremony.Attendees, policy.PawnID(a.GetId()))
		}
		out.Ceremonies = append(out.Ceremonies, ceremony)
	}
	thrones := map[string]bool{}
	for _, row := range v.Thrones {
		id, owner := row.GetThing().GetId(), row.GetOwner().GetId()
		if validID(id) != nil || owner != "" && validID(owner) != nil || row.DefName != nil && validID(row.GetDefName()) != nil || thrones[id] {
			return policy.RoyaltyColony{}, contract("invalid royalty throne")
		}
		thrones[id] = true
		out.Thrones = append(out.Thrones, policy.RoyalThrone{ID: id, Def: row.GetDefName(), Owner: policy.PawnID(owner)})
	}
	return out, nil
}
