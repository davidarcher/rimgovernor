package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Unique short names (#1310, epic #1292): per-pawn policies are labelled by
// a pawn's short name, so every owned pawn (colonists, slaves, prisoners,
// named animals) holds one no other owned pawn holds. On a collision the
// oldest holder keeps it and every newer one is renamed; native draws the
// new name from the pawn's own name bank, never a numbered one.

// OwnedName is one owned named pawn: its short name and thingIDNumber, which
// orders pawns by creation.
type OwnedName struct {
	Pawn    PawnID
	Short   string
	ThingID int
}

// NicknameChanges are the renames owed: every pawn whose short name an
// older owned pawn already holds, compared case-insensitively.
func NicknameChanges(names []OwnedName) []domain.PawnSettings {
	rows := append([]OwnedName(nil), names...)
	sort.SliceStable(rows, func(i, j int) bool { return rows[i].ThingID < rows[j].ThingID })
	held := map[string]bool{}
	var out []domain.PawnSettings
	for _, row := range rows {
		key := strings.ToLower(strings.TrimSpace(row.Short))
		if key == "" {
			continue
		}
		if !held[key] {
			held[key] = true
			continue
		}
		if s, err := domain.NewNicknameSetting(domain.PawnID(row.Pawn), row.Short); err == nil {
			out = append(out, s)
		}
	}
	return out
}

// NamesOwed is the review's NamesOwed fact: a rename is owed. An unknown
// census owes nothing.
func NamesOwed(names domain.Fact[[]OwnedName]) domain.Fact[bool] {
	rows, known := names.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(NicknameChanges(rows)) > 0)
}
