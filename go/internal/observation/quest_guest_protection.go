package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"slices"
)

// stampQuestGuestProtection derives the open meeting's protection on each read;
// rescue and ordinary tending stay available when operations are excluded.
func stampQuestGuestProtection(f *policy.RoundsFacts) {
	ids := policy.ProtectedQuestGuestIDs(f.QuestOffers)
	if rows, known := f.Custody.Value(); known {
		rows = slices.Clone(rows)
		for i := range rows {
			rows[i].QuestProtected = ids[rows[i].Pawn]
		}
		f.Custody = domain.Known(rows)
	}
	if rows, known := f.MedicalPawns.Value(); known {
		rows = slices.Clone(rows)
		for i := range rows {
			rows[i].QuestProtected = ids[domain.PawnID(rows[i].ID)]
		}
		f.MedicalPawns = domain.Known(rows)
	}
}
