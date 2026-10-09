package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func fact[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}

func healthIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}

// CareConditions decodes a health row's conditions and life threat, the
// inputs of the care planners. A visible-only or incomplete hediff
// list leaves the conditions unknown: it cannot prove a condition absent.
func CareConditions(h *o.PawnHealth) (domain.Fact[[]policy.CareCondition], domain.Fact[bool]) {
	unknown := domain.Unknown[[]policy.CareCondition]()
	if h == nil {
		return unknown, domain.Unknown[bool]()
	}
	life := fact(h.LifeThreatening)
	c := h.HediffCompleteness
	if c == nil || c.GetFiltered() != 0 || h.HiddenHediffs == nil || h.GetHiddenHediffs() != 0 || healthIssue(h.Issues, "hediffs") {
		return unknown, life
	}
	rows := make([]policy.CareCondition, 0, len(h.Hediffs))
	for _, condition := range h.Hediffs {
		if condition == nil {
			return unknown, life
		}
		row := policy.CareCondition{
			Severity: fact(condition.Severity), SeverityPerDay: fact(condition.SeverityPerDay),
			Immunity: fact(condition.Immunity), ImmunityPerDay: fact(condition.ImmunityPerDay),
			Tended: fact(condition.Tended), TendQuality: fact(condition.TendQuality),
		}
		if condition.PartIndex != nil {
			row.PartIndex = domain.Known(int(condition.GetPartIndex()))
		}
		row.DefName = fact(condition.DefName)
		rows = append(rows, row)
	}
	return domain.Known(rows), life
}
