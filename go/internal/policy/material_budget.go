package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// IngredientReservation is what one pawn's live bill job has promised:
// the spawned ingredients it has queued or placed.
type IngredientReservation struct {
	Pawn  PawnID
	Items []Amount
}

// MaterialHolds is what free stock already owes elsewhere: every standing
// blueprint's and frame's undelivered material, and every other pawn's
// live bill-job ingredients (worker's own job excluded; "" excludes
// none). An unknown census contributes nothing.
func MaterialHolds(deficit domain.Fact[map[Resource]int64], reservations domain.Fact[[]IngredientReservation], worker PawnID) []Amount {
	total := map[Resource]int64{}
	if rows, known := deficit.Value(); known {
		for resource, n := range rows {
			total[resource] += n
		}
	}
	if rows, known := reservations.Value(); known {
		for _, r := range rows {
			if worker != "" && r.Pawn == worker {
				continue
			}
			for _, item := range r.Items {
				total[item.Resource] += item.Count
			}
		}
	}
	out := make([]Amount, 0, len(total))
	for resource, n := range total {
		if n > 0 {
			out = append(out, Amount{resource, n})
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return out
}

// MaterialBudget is free stock per definition: the reachable,
// unforbidden item census less MaterialHolds. A held definition absent
// from the census goes negative: it is owed more than exists. Unknown
// while the stock census is.
func MaterialBudget(stock domain.Fact[[]Amount], deficit domain.Fact[map[Resource]int64], reservations domain.Fact[[]IngredientReservation], worker PawnID) domain.Fact[map[Resource]int64] {
	rows, known := stock.Value()
	if !known {
		return domain.Unknown[map[Resource]int64]()
	}
	out := map[Resource]int64{}
	for _, row := range rows {
		out[row.Resource] += row.Count
	}
	for _, hold := range MaterialHolds(deficit, reservations, worker) {
		out[hold.Resource] -= hold.Count
	}
	return domain.Known(out)
}
