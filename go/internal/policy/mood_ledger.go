package policy

import (
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// thoughtOwners are the concerns whose facility removes the thought: the
// audit table's hand-kept owner column (thought_triggers.tsv), the one place
// a fix is declared. A def with no row, or an empty owner, has none.
func thoughtOwners(def string) []ConcernID {
	row, ok := ThoughtAuditRow(def)
	if !ok || row.Owner == "" {
		return nil
	}
	var owners []ConcernID
	for _, owner := range strings.Split(row.Owner, ";") {
		owners = append(owners, ConcernID(owner))
	}
	return owners
}

// MoodProvisionConcern reports whether the audit table names the concern as
// the owner of some thought.
func MoodProvisionConcern(goal ConcernID) bool {
	for _, row := range thoughtAuditRows {
		if slices.Contains(strings.Split(row.Owner, ";"), string(goal)) {
			return true
		}
	}
	return false
}

// moodExpectationOrder is the vanilla ExpectationDef ladder, lowest first;
// a ThoughtFacts.MinExpectation or a pawn level outside it is unknown.
var moodExpectationOrder = []string{"ExtremelyLow", "VeryLow", "Low", "Moderate", "High", "SkyHigh"}

// MoodLedgerPawn is one colonist as the ledger reads it: the observed thought
// rows plus the attributes immunity and expectation gating need. An unknown
// attribute never excludes a thought; it marks the source unverified.
type MoodLedgerPawn struct {
	ID               PawnID
	Thoughts         domain.Fact[[]MoodThought]
	Traits, Precepts domain.Fact[[]string]
	Expectation      domain.Fact[string]
}

// MoodPawnLoss is the mood one pawn loses per thought: negative offsets that
// apply to it, most negative first. Unknown when its thoughts were unreadable.
type MoodPawnLoss struct {
	ID   PawnID
	Lost domain.Fact[[]MoodThought]
}

// MoodLedgerSource is one thought's colony total: pawns affected x offset.
type MoodLedgerSource struct {
	Def    string
	Pawns  int
	Lost   float64
	Owners []ConcernID
	// Unverified counts affected pawns whose immunity or expectation gating
	// could not be checked (no ThoughtFacts for the def, or an unknown pawn
	// attribute); their loss is still counted.
	Unverified int
}

// MoodLedgerExpectation is the expectation level as a visible source: how
// many pawns sit at each level. Shown only; no concern fixes it.
type MoodLedgerExpectation struct {
	Level string
	Pawns int
}

// MoodLedger answers where mood is lost. Sources rank all negative thoughts;
// Unowned is the subset with no audited owner (empty or unclassified), the
// next gap-fill chosen by mood lost.
type MoodLedger struct {
	Pawns       []MoodPawnLoss
	Sources     []MoodLedgerSource
	Unowned     []MoodLedgerSource
	Expectation []MoodLedgerExpectation
	// UnknownPawns counts pawns whose thoughts were unreadable.
	UnknownPawns int
}

// thoughtApplies reports whether the def's facts let the pawn carry the
// thought; known is false when a needed pawn attribute is unreadable.
func thoughtApplies(f ThoughtFacts, p MoodLedgerPawn) (applies, known bool) {
	known = true
	traits, tk := p.Traits.Value()
	precepts, pk := p.Precepts.Value()
	for _, t := range f.NullifyingTraits {
		if !tk {
			known = false
		} else if slices.Contains(traits, t) {
			return false, true
		}
	}
	for _, t := range f.NullifyingPrecepts {
		if !pk {
			known = false
		} else if slices.Contains(precepts, t) {
			return false, true
		}
	}
	if len(f.RequiredTraits) > 0 {
		if !tk {
			known = false
		} else if !slices.ContainsFunc(f.RequiredTraits, func(t string) bool { return slices.Contains(traits, t) }) {
			return false, true
		}
	}
	if f.MinExpectation != "" {
		level, lk := p.Expectation.Value()
		have, min := slices.Index(moodExpectationOrder, level), slices.Index(moodExpectationOrder, f.MinExpectation)
		if !lk || have < 0 || min < 0 {
			known = false
		} else if have < min {
			return false, true
		}
	}
	return true, known
}

// BuildMoodLedger turns pawns' thought rows and the defs' ThoughtFacts into
// per-pawn loss and ranked colony sources. Only negative offsets are loss.
// A def with no facts keeps its loss, unverified, and is owned only if the
// audit table says so; nothing is dropped or zeroed for missing facts.
func BuildMoodLedger(pawns []MoodLedgerPawn, facts map[string]ThoughtFacts) MoodLedger {
	var ledger MoodLedger
	sources := map[string]*MoodLedgerSource{}
	levels := map[string]int{}
	for _, p := range pawns {
		if level, ok := p.Expectation.Value(); ok {
			levels[level]++
		}
		rows, known := p.Thoughts.Value()
		if !known {
			ledger.UnknownPawns++
			ledger.Pawns = append(ledger.Pawns, MoodPawnLoss{ID: p.ID, Lost: domain.Unknown[[]MoodThought]()})
			continue
		}
		lost := []MoodThought{}
		for _, t := range rows {
			if t.Offset >= 0 {
				continue
			}
			verified := false
			if f, ok := facts[t.Def]; ok {
				applies, k := thoughtApplies(f, p)
				if !applies {
					continue
				}
				verified = k
			}
			lost = append(lost, t)
			s := sources[t.Def]
			if s == nil {
				s = &MoodLedgerSource{Def: t.Def, Owners: thoughtOwners(t.Def)}
				sources[t.Def] = s
			}
			s.Pawns++
			s.Lost += t.Offset
			if !verified {
				s.Unverified++
			}
		}
		sortThoughts(lost)
		ledger.Pawns = append(ledger.Pawns, MoodPawnLoss{ID: p.ID, Lost: domain.Known(lost)})
	}
	for _, s := range sources {
		ledger.Sources = append(ledger.Sources, *s)
	}
	sort.Slice(ledger.Sources, func(i, j int) bool {
		a, b := ledger.Sources[i], ledger.Sources[j]
		if a.Lost != b.Lost {
			return a.Lost < b.Lost
		}
		return a.Def < b.Def
	})
	for _, s := range ledger.Sources {
		if len(s.Owners) == 0 {
			ledger.Unowned = append(ledger.Unowned, s)
		}
	}
	for level, n := range levels {
		ledger.Expectation = append(ledger.Expectation, MoodLedgerExpectation{level, n})
	}
	sort.Slice(ledger.Expectation, func(i, j int) bool {
		a, b := ledger.Expectation[i], ledger.Expectation[j]
		if a.Pawns != b.Pawns {
			return a.Pawns > b.Pawns
		}
		return a.Level < b.Level
	})
	return ledger
}

func sortThoughts(rows []MoodThought) {
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].Offset != rows[j].Offset {
			return rows[i].Offset < rows[j].Offset
		}
		return rows[i].Def < rows[j].Def
	})
}

// moodUnowned is the pawn's negative thoughts with no audited owner, most
// negative first; the ledger's unowned bucket for one pawn.
func moodUnowned(f domain.Fact[[]MoodThought]) []MoodThought {
	rows, known := f.Value()
	if !known {
		return nil
	}
	var result []MoodThought
	for _, t := range rows {
		if t.Offset < 0 && len(thoughtOwners(t.Def)) == 0 {
			result = append(result, t)
		}
	}
	sortThoughts(result)
	return result
}
