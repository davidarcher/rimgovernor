package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"slices"
)

func stampQuestRefugees(f *policy.RoundsFacts) {
	refugees := policy.QuestRefugeeIDs(f.QuestOffers)
	if rows, known := f.Custody.Value(); known {
		rows = slices.Clone(rows)
		for i := range rows {
			rows[i].QuestRefugee = refugees[rows[i].Pawn] != ""
		}
		f.Custody = domain.Known(rows)
	}
}
