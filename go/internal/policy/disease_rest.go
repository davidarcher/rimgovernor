package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DiseaseProjection is a forecast in game days, not evidence of recovery.
// A nonpositive rate has no projected crossing (positive infinity).
type DiseaseProjection struct {
	DaysToLethal, DaysToImmune float64
}

func ProjectDisease(c CareCondition) domain.Fact[DiseaseProjection] {
	s, sk := c.Severity.Value()
	i, ik := c.Immunity.Value()
	sr, srk := c.SeverityPerDay.Value()
	ir, irk := c.ImmunityPerDay.Value()
	if !sk || !ik || !srk || !irk || s < 0 || i < 0 || i > 1 {
		return domain.Unknown[DiseaseProjection]()
	}
	for _, v := range []float64{s, i, sr, ir} {
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return domain.Unknown[DiseaseProjection]()
		}
	}
	days := func(value, rate float64) float64 {
		if value >= 1 {
			return 0
		}
		if rate <= 0 {
			return math.Inf(1)
		}
		return (1 - value) / rate
	}
	return domain.Known(DiseaseProjection{days(s, sr), days(i, ir)})
}

func (p DiseaseProjection) NeedsRest() bool {
	return p.DaysToImmune > 0 && !math.IsInf(p.DaysToLethal, 1) && p.DaysToLethal-p.DaysToImmune < 1
}

// DiseaseRest holds the conditions that triggered rest. Improved rates while
// resting do not release it: every tracked condition must be immune or absent
// from a complete census. Missing pawns and unknown health retain the hold.
type DiseaseRest struct {
	Pawn       PawnID
	Conditions []string
}

func validateDiseaseRest(rows []DiseaseRest) error {
	if len(rows) > 256 {
		return errors.New("disease rest history exceeds bound")
	}
	for i, row := range rows {
		if !foodID(string(row.Pawn)) || i > 0 && rows[i-1].Pawn >= row.Pawn || len(row.Conditions) == 0 || len(row.Conditions) > 256 {
			return errors.New("invalid disease rest history")
		}
		for j, name := range row.Conditions {
			if !validResource(Resource(name)) || j > 0 && row.Conditions[j-1] >= name {
				return errors.New("invalid disease rest condition")
			}
		}
	}
	return nil
}

func ReviewDiseaseRest(observed domain.Fact[[]CarePawn], previous []DiseaseRest) ([]DiseaseRest, error) {
	if err := validateDiseaseRest(previous); err != nil {
		return nil, err
	}
	held := map[PawnID]map[string]bool{}
	for _, row := range previous {
		held[row.Pawn] = map[string]bool{}
		for _, name := range row.Conditions {
			held[row.Pawn][name] = true
		}
	}
	pawns, known := observed.Value()
	if !known {
		return append([]DiseaseRest(nil), previous...), nil
	}
	if len(pawns) > 256 {
		return nil, errors.New("disease census exceeds bound")
	}
	seen := map[PawnID]bool{}
	for _, pawn := range pawns {
		if !foodID(string(pawn.ID)) || seen[pawn.ID] {
			return nil, errors.New("invalid disease pawn")
		}
		seen[pawn.ID] = true
		dead, dk := pawn.Dead.Value()
		conditions, ck := pawn.Conditions.Value()
		if !dk || dead || !ck {
			continue
		}
		if len(conditions) > 256 {
			return nil, errors.New("disease conditions exceed bound")
		}
		next := map[string]bool{}
		unnamed := false
		for _, c := range conditions {
			name, nk := c.DefName.Value()
			if !nk || !validResource(Resource(name)) {
				unnamed = true
				continue
			}
			immune, ik := c.Immunity.Value()
			if held[pawn.ID][name] && (!ik || math.IsNaN(immune) || math.IsInf(immune, 0) || immune < 1) {
				next[name] = true
			}
			if projection, known := ProjectDisease(c).Value(); known && projection.NeedsRest() {
				next[name] = true
			}
		}
		if unnamed {
			for name := range held[pawn.ID] {
				next[name] = true
			}
		}
		held[pawn.ID] = next
	}
	var result []DiseaseRest
	for pawn, conditions := range held {
		if len(conditions) == 0 {
			continue
		}
		row := DiseaseRest{Pawn: pawn}
		for name := range conditions {
			row.Conditions = append(row.Conditions, name)
		}
		sort.Strings(row.Conditions)
		result = append(result, row)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Pawn < result[j].Pawn })
	return result, validateDiseaseRest(result)
}
