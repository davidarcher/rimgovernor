package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func routineMedical(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, snapshot *o.PawnSnapshot) domain.Fact[[]policy.CarePawn] {
	unknown := domain.Unknown[[]policy.CarePawn]()
	complete, known := emergency.ColonistsComplete.Value()
	if !known || !complete || colony == nil || colony.ColonistCount == nil || snapshot == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(snapshot.Pawns) != len(emergency.Colonists) {
		return unknown
	}
	byID := map[string]*o.PawnState{}
	for _, row := range snapshot.Pawns {
		if row == nil || row.Pawn == nil || byID[row.Pawn.GetId()] != nil {
			return unknown
		}
		byID[row.Pawn.GetId()] = row
	}
	rows := make([]policy.CarePawn, 0, len(snapshot.Pawns))
	for _, pawn := range emergency.Colonists {
		row := byID[string(pawn.ID)]
		dead, dk := pawn.Dead.Value()
		downed, nk := pawn.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return unknown
		}
		p := policy.CarePawn{ID: pawn.ID, Dead: domain.Known(dead)}
		if settings := row.Settings; settings != nil {
			p.Care = optional(settings.MedicalCare)
			if settings.Snapshot != nil {
				p.SettingsToken = optional(settings.Snapshot.Token)
			}
		}
		if h := row.Health; h != nil && !hasIssue(row.Issues, "health") {
			p.LifeThreatening = optional(h.LifeThreatening)
			p.NeedsRest, p.NeedsTend = optional(h.ShouldSeekMedicalRest), optional(h.NeedsTend)
			c := h.HediffCompleteness
			// A visible-only or incomplete list cannot prove that bad conditions
			// have resolved. Other health details remain independent evidence.
			if c != nil && c.Page != nil && c.Page.Complete != nil && c.Page.GetComplete() && c.Matched != nil && c.Returned != nil && c.Filtered != nil && c.Unreadable != nil && c.GetMatched() == uint64(len(h.Hediffs)) && c.GetReturned() == uint64(len(h.Hediffs)) && c.GetFiltered() == 0 && c.GetUnreadable() == 0 && h.HiddenHediffs != nil && h.GetHiddenHediffs() == 0 && !hasIssue(h.Issues, "hediffs") {
				conditions := make([]policy.CareCondition, 0, len(h.Hediffs))
				for _, condition := range h.Hediffs {
					if condition == nil {
						continue
					}
					detail := policy.CareCondition{
						Severity: optional(condition.Severity), SeverityPerDay: optional(condition.SeverityPerDay),
						Immunity: optional(condition.Immunity), ImmunityPerDay: optional(condition.ImmunityPerDay),
						Tended: optional(condition.Tended), TendQuality: optional(condition.TendQuality),
					}
					if condition.Definition != nil {
						detail.DefName = optional(condition.Definition.DefName)
					}
					conditions = append(conditions, detail)
				}
				if len(conditions) == len(h.Hediffs) {
					p.Conditions = domain.Known(conditions)
				}
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
	return domain.Known(rows)
}
