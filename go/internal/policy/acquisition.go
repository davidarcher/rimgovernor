package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AcquisitionSource is an observed native-approved source, not inventory.
// Definition is the source's own native definition name (the plant or the
// animal, not the harvested resource); a hunt row of a pest race (Pest, the
// race row's) is a pest hunt: food false, no nutrition.
type AcquisitionSource struct {
	ID, Resource, Token          string
	Definition                   string
	Cell                         domain.Cell
	Tree, Food, Designated, Hunt bool
	Yield, NutritionYield        float64
	RevengeChance, WeaponRange   float64
	HerdSize                     int
	MeleeOnly, Downed            bool
	// BodySize, Sleeping and Predator describe a hunt row for squad planning.
	BodySize           float64
	Sleeping, Predator bool
	// Products are what butchering the animal yields besides meat (leather),
	// from its race row.
	Products []SourceProduct `json:",omitempty"`
	// Pest is the source's race row flag (AnimalRace.Pest, #1722).
	Pest bool `json:",omitempty"`
	// DesignatedTick is the tick native first saw the designation (reset on
	// load); set only when Designated. Taken: a pawn's reservation or a
	// colonist's current job targets the source (#1043).
	DesignatedTick domain.Tick
	Taken          bool
}

// SelectAcquisition prefers forage, then downed prey and lower herd revenge cost.
// Equal-cost hunts go meatiest first; otherwise native ordering holds. Pending yield and unresolved
// sources prevent duplicate work but never count as recovered stock.
func SelectAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], food bool, held map[string]bool, huntSlots ...domain.Fact[int]) ([]AcquisitionSource, error) {
	accept := func(row AcquisitionSource) (float64, bool) {
		if food {
			return row.NutritionYield, row.Food && row.NutritionYield > 0
		}
		return row.Yield, row.Tree && row.Resource == "WoodLog"
	}
	return selectAcquisition(sources, deficit, pending, accept, held, huntSlots...)
}

// SelectResourceAcquisition is SelectAcquisition for one harvested
// definition (herbal medicine from wild healroot): every non-hunt source
// yielding exactly that resource counts by its unit yield.
func SelectResourceAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], resource Resource, held map[string]bool) ([]AcquisitionSource, error) {
	if !validResource(resource) {
		return nil, errors.New("invalid acquisition resource")
	}
	accept := func(row AcquisitionSource) (float64, bool) {
		return row.Yield, !row.Hunt && Resource(row.Resource) == resource
	}
	return selectAcquisition(sources, deficit, pending, accept, held)
}

func selectAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], accept func(AcquisitionSource) (float64, bool), held map[string]bool, huntSlots ...domain.Fact[int]) ([]AcquisitionSource, error) {
	rows, known := sources.Value()
	need, nk := deficit.Value()
	outstanding, pk := pending.Value()
	if !known || !nk || !pk || !foodNumber(need) || !foodNumber(outstanding) {
		return nil, errors.New("acquisition facts unavailable")
	}
	slots := 0
	if len(huntSlots) > 1 {
		return nil, errors.New("multiple hunting budgets")
	}
	if len(huntSlots) == 1 {
		if n, known := huntSlots[0].Value(); known {
			if n < 0 || n > MaxHuntRows {
				return nil, errors.New("invalid hunting budget")
			}
			slots = n
		}
	}
	seen := map[string]bool{}
	for _, row := range rows {
		if !foodNumber(row.RevengeChance) || row.RevengeChance > 1 || row.HerdSize < 0 || row.HerdSize > 65536 || !foodNumber(row.WeaponRange) || !foodNumber(row.BodySize) || !foodID(row.ID) || !foodID(row.Resource) || !foodID(row.Token) || seen[row.ID] || row.Cell.X < 0 || row.Cell.Z < 0 || !foodNumber(row.Yield) || row.Yield <= 0 || !foodNumber(row.NutritionYield) || !row.Food && row.NutritionYield != 0 || row.Hunt && (row.Tree || row.Yield != 1 || !row.Food && !row.Pest) {
			return nil, errors.New("invalid acquisition source")
		}
		seen[row.ID] = true
	}
	rows = append([]AcquisitionSource(nil), rows...)
	sort.SliceStable(rows, func(i, j int) bool {
		a, b := rows[i], rows[j]
		if a.Hunt != b.Hunt {
			return !a.Hunt
		}
		if !a.Hunt {
			return false
		}
		if a.Downed != b.Downed {
			return a.Downed
		}
		if a.HuntRevengeCost() != b.HuntRevengeCost() {
			return a.HuntRevengeCost() < b.HuntRevengeCost()
		}
		// Hunt slots are few: of equally safe prey the meatier goes first, so a
		// squirrel does not take the slot a deer could.
		return a.NutritionYield > b.NutritionYield
	})
	remaining := math.Max(0, need-outstanding)
	selected := []AcquisitionSource{}
	others := 0
	for _, row := range rows {
		if remaining <= 0 {
			break
		}
		if row.Designated || held[row.ID] {
			continue
		}
		if row.Hunt && (slots == 0 || row.Retaliates()) {
			continue
		}
		// The row cap bounds the non-hunt rows; hunts are bounded by the
		// hunters' budget (#2170), so their count follows the nutrition gap.
		if !row.Hunt && others == MaxCatalogSelection {
			continue
		}
		amount, ok := accept(row)
		if !ok {
			continue
		}
		if row.Hunt {
			slots--
		} else {
			others++
		}
		selected = append(selected, row)
		remaining -= amount
	}
	return selected, nil
}

// MaxHuntRevengeChance is the highest manhunter-on-harm chance a standing
// animal may carry and still be hunted. Above it (moose, boar, elk...) one
// wounding shot risks a revenge charge the colony's one or two hunters
// cannot absorb. Downed prey cannot retaliate.
const MaxHuntRevengeChance = 0.2

// Retaliates reports a hunt a lone hunter must not designate: a predator, or
// a standing animal whose revenge chance exceeds MaxHuntRevengeChance.
// Selection never designates it; a squad may (SquadPrey).
func (s AcquisitionSource) Retaliates() bool {
	return s.Hunt && (s.Predator || !s.Downed && s.RevengeChance > MaxHuntRevengeChance)
}

// SquadPrey reports an open hunt row a squad may target, retaliating or not.
func (s AcquisitionSource) SquadPrey() bool {
	return s.Hunt && !s.Designated && !s.Taken
}

// HuntRevengeCost is expected retaliation exposure, before the channel risk cap.
func (s AcquisitionSource) HuntRevengeCost() float64 {
	if s.Downed {
		return 0
	}
	return s.RevengeChance * float64(max(1, s.HerdSize))
}
