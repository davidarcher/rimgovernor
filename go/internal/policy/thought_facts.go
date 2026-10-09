package policy

import (
	_ "embed"
	"strings"
)

// ThoughtUnclassified is the dependency of a def the audit table has no row
// for (a modded thought, a def new since the last regeneration, or a name
// that is not a ThoughtDef).
const ThoughtUnclassified = "unclassified"

// ThoughtFacts is one ThoughtDef as the mood ledger reads it: the bridge
// projects the catalog row and the audit table into it, so policy never
// sees the catalog. Immunity is data, not a verdict: the consumer matches
// the pawn's traits and precepts.
type ThoughtFacts struct {
	Def string
	// WorstOffset is the lowest BaseMoodEffect of the def's stages (the most
	// negative, or the least positive when none is negative); 0 without stages.
	WorstOffset float64
	// NullifyingTraits and NullifyingPrecepts make a pawn immune; RequiredTraits
	// are traits a pawn must have for the thought to apply.
	NullifyingTraits   []string
	NullifyingPrecepts []string
	RequiredTraits     []string
	// MinExpectation is the lowest expectation that grants the thought, empty
	// when it applies at any.
	MinExpectation string
	// Dependency is the audit table's ';'-joined state tokens, or
	// ThoughtUnclassified. Owner is the table's hand-kept concern, empty when
	// no concern removes the thought.
	Dependency string
	Owner      string
}

// ThoughtAudit is one row of thought_triggers.tsv.
type ThoughtAudit struct {
	Dependency string
	Owner      string
}

//go:embed thought_triggers.tsv
var thoughtTriggersTSV string

// thoughtAuditRows is the parsed table, built once at package init.
var thoughtAuditRows = func() map[string]ThoughtAudit {
	rows := map[string]ThoughtAudit{}
	for _, line := range strings.Split(thoughtTriggersTSV, "\n")[1:] {
		if f := strings.Split(strings.TrimRight(line, "\r"), "\t"); len(f) == 5 {
			rows[f[0]] = ThoughtAudit{Dependency: f[3], Owner: f[4]}
		}
	}
	return rows
}()

// ThoughtAuditRow is the generated thought-to-trigger row of the def, false
// when the table has none (go run ./cmd/thoughtaudit regenerates the table;
// see docs/developers/contracts/mood-control.md).
func ThoughtAuditRow(def string) (ThoughtAudit, bool) {
	row, ok := thoughtAuditRows[def]
	return row, ok
}
