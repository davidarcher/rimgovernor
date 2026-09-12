package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StartingSupplies retains only the first complete native startup census.
// Pending cells describe an observed need, not ownership or write permission.
type StartingSupplies struct {
	Initialized bool
	Pending     []domain.Cell
}

func supplyCellLess(a, b domain.Cell) bool { return a.Z < b.Z || a.Z == b.Z && a.X < b.X }

func (s StartingSupplies) Validate() error {
	if len(s.Pending) > 256 || !s.Initialized && len(s.Pending) > 0 {
		return errors.New("invalid starting supply history")
	}
	for i, c := range s.Pending {
		if c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || i > 0 && !supplyCellLess(s.Pending[i-1], c) {
			return errors.New("invalid starting supply cell")
		}
	}
	return nil
}

// ReviewStartingSupplies never adopts later forbids or revives released cells.
// An unavailable read preserves history without claiming current recovery.
func ReviewStartingSupplies(observed domain.Fact[[]domain.Cell], previous StartingSupplies) (StartingSupplies, domain.Fact[bool], error) {
	if err := previous.Validate(); err != nil {
		return StartingSupplies{}, domain.Unknown[bool](), err
	}
	previous.Pending = append([]domain.Cell(nil), previous.Pending...)
	rows, known := observed.Value()
	if !known {
		return previous, domain.Unknown[bool](), nil
	}
	rows = append([]domain.Cell(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return supplyCellLess(rows[i], rows[j]) })
	current := StartingSupplies{Initialized: true, Pending: rows}
	if err := current.Validate(); err != nil {
		return StartingSupplies{}, domain.Unknown[bool](), err
	}
	if !previous.Initialized {
		return current, domain.Known(len(rows) > 0), nil
	}
	seen := map[domain.Cell]bool{}
	for _, c := range rows {
		seen[c] = true
	}
	r := StartingSupplies{Initialized: true}
	for _, c := range previous.Pending {
		if seen[c] {
			r.Pending = append(r.Pending, c)
		}
	}
	return r, domain.Known(len(r.Pending) > 0), nil
}
