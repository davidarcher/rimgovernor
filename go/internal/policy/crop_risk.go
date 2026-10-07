package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CropSeason is the shared sky a crop channel is priced under: the growing
// calendar and the active game conditions. Unknown facts price no risk and no
// pause, never a guessed one.
type CropSeason struct {
	Calendar   domain.Fact[Calendar]
	Conditions domain.Fact[[]DisasterCondition]
}

// FieldExposure is what a planted field's risks derive from: the held cells of
// its zone and the share under open sky, and its planted cells and the
// blighted plants standing in them. Each fact is unknown until observed.
type FieldExposure struct {
	ZoneCells, UnroofedCells domain.Fact[int64]
	Planted, Blighted        domain.Fact[int64]
}

// CropPauseDays is the observed remaining duration, in game days, of the
// conditions that stop crops growing: an eclipse, a volcanic winter or a cold
// snap with a native remaining-duration read. Zero when none is active or the
// census is unknown. A crop's harvest lead extends by it.
func CropPauseDays(conditions domain.Fact[[]DisasterCondition]) float64 {
	ticks, known := ConditionRemainingTicks(conditions, ConditionEclipse, ConditionVolcanicWinter, ConditionColdSnap).Value()
	if !known || ticks <= 0 {
		return 0
	}
	return float64(ticks) / domain.TicksPerDay
}

// toxicFalloutActive is whether the toxic-fallout condition is on the map;
// unknown while the condition census is.
func toxicFalloutActive(conditions domain.Fact[[]DisasterCondition]) domain.Fact[bool] {
	rows, known := conditions.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, c := range rows {
		if c.Definition == ConditionToxicFallout {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// share is part/whole as a fact in [0, 1]; unknown while either count is, the
// whole is not positive or the part exceeds it.
func share(part, whole domain.Fact[int64]) domain.Fact[float64] {
	p, pk := part.Value()
	w, wk := whole.Value()
	if !pk || !wk || w <= 0 || p < 0 || p > w {
		return domain.Unknown[float64]()
	}
	return domain.Known(float64(p) / float64(w))
}

// FalloutRisk is the expected fraction of a field's delivery toxic fallout
// takes: while the condition is on, the plants under open sky die, so the
// weight is the unroofed share of the field's cells; zero without the
// condition. Unknown while the census or the roof data is.
func FalloutRisk(conditions domain.Fact[[]DisasterCondition], e FieldExposure) domain.Fact[float64] {
	active, ak := toxicFalloutActive(conditions).Value()
	if !ak {
		return domain.Unknown[float64]()
	}
	if !active {
		return domain.Known(0.0)
	}
	return share(e.UnroofedCells, e.ZoneCells)
}

// BlightRisk is the expected fraction of a field's delivery blight takes: the
// blighted share of its planted cells. Unknown while a count is.
func BlightRisk(e FieldExposure) domain.Fact[float64] {
	return share(e.Blighted, e.Planted)
}

// FrostRisk is the expected fraction of a field's delivery frost takes: when
// the harvest lead outlasts the growing days left, the share of the lead window
// spent in the non-growing stretch, for the part of the field under open sky.
// Zero while the harvest lands before the frost. Unknown while the lead, the
// calendar or the roof data is.
func FrostRisk(lead domain.Fact[float64], calendar domain.Fact[Calendar], e FieldExposure) domain.Fact[float64] {
	days, dk := lead.Value()
	c, ck := calendar.Value()
	open, ok := share(e.UnroofedCells, e.ZoneCells).Value()
	if !dk || !ck || !ok || !c.Valid() || math.IsNaN(days) || days <= 0 {
		return domain.Unknown[float64]()
	}
	if days <= c.GrowingDaysRemaining {
		return domain.Known(0.0)
	}
	return domain.Known(open * math.Min(1, c.NonGrowingDays/days))
}

// cropRisks lists the known, positive risks of a field's expected fallout,
// blight and frost; an unknown weight is left out, never priced as zero or as
// a loss.
func cropRisks(season CropSeason, lead domain.Fact[float64], e FieldExposure) []CandidateRisk {
	var out []CandidateRisk
	for _, r := range []struct {
		kind   CandidateRiskKind
		weight domain.Fact[float64]
	}{
		{CandidateFallout, FalloutRisk(season.Conditions, e)},
		{CandidateBlight, BlightRisk(e)},
		{CandidateFrost, FrostRisk(lead, season.Calendar, e)},
	} {
		if w, known := r.weight.Value(); known && w > 0 {
			out = append(out, CandidateRisk{r.kind, math.Min(1, w)})
		}
	}
	return out
}

// withCropPause is a harvest lead pushed back by the crop pause, capped at a
// year; an unknown lead stays unknown.
func withCropPause(lead domain.Fact[float64], conditions domain.Fact[[]DisasterCondition]) domain.Fact[float64] {
	days, known := lead.Value()
	if !known || !foodNumber(days) {
		return lead
	}
	return domain.Known(math.Min(YearDays, days+CropPauseDays(conditions)))
}
