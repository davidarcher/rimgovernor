package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func roundsMedical(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, snapshot *o.PawnSnapshot, catalog *bridge.DefinitionCatalog) (domain.Fact[[]policy.CarePawn], error) {
	unknown := domain.Unknown[[]policy.CarePawn]()
	complete, known := emergency.ColonistsComplete.Value()
	if !known || !complete || colony == nil || colony.ColonistCount == nil || snapshot == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(snapshot.Pawns) != len(emergency.Colonists) {
		return unknown, nil
	}
	byID := map[string]*o.PawnState{}
	for _, row := range snapshot.Pawns {
		if row == nil || row.Pawn == nil || byID[row.Pawn.GetId()] != nil {
			return unknown, nil
		}
		byID[row.Pawn.GetId()] = row
	}
	rows := make([]policy.CarePawn, 0, len(snapshot.Pawns))
	for _, pawn := range emergency.Colonists {
		row := byID[string(pawn.ID)]
		dead, dk := pawn.Dead.Value()
		downed, nk := pawn.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return unknown, nil
		}
		p := policy.CarePawn{ID: pawn.ID, Dead: domain.Known(dead)}
		if settings := row.Settings; settings != nil {
			p.Care = careName(settings.MedicalCare)
		}
		if h := row.Health; h != nil && !hasIssue(row.Issues, "health") {
			var err error
			if p.MissingParts, p.Operations, err = bridge.SurgeryFacts(h, catalog); err != nil {
				return unknown, err
			}
			p.QueuedSurgeries = bridge.QueuedSurgeries(h)
			if p.InstalledParts, err = bridge.InstalledParts(h, catalog); err != nil {
				return unknown, err
			}
			p.QueuedRecipes = bridge.QueuedSurgeryRecipes(h)
			if p.QueuedItems, err = bridge.QueuedSurgeryItems(p.QueuedRecipes, catalog); err != nil {
				return unknown, err
			}
			p.Conditions, p.LifeThreatening = bridge.CareConditions(h)
			p.NeedsRest, p.NeedsTend = optional(h.ShouldSeekMedicalRest), optional(h.NeedsTend)
			c := h.HediffCompleteness
			// A visible-only or incomplete list cannot prove that bad conditions
			// have resolved. Other health details remain independent evidence.
			if c != nil && c.GetFiltered() == 0 && h.HiddenHediffs != nil && h.GetHiddenHediffs() == 0 && !hasIssue(h.Issues, "hediffs") {
				bad, known := false, true
				for _, condition := range h.Hediffs {
					if condition == nil || condition.Bad == nil {
						known = false
						break
					}
					bad = bad || condition.GetBad()
				}
				if known {
					p.BadConditions = domain.Known(bad)
				}
			}
		}
		rows = append(rows, p)
	}
	return domain.Known(rows), nil
}
