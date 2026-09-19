package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StartingSupply is one forbidden starting stack at the cell the latest
// census saw it. Thing identity is the cohort key; the cell only tells the
// planner where to read it, so a stack a builder hauls aside stays cohort work.
type StartingSupply struct {
	Thing, Definition string
	Cell              domain.Cell
	Forbid            bool
}

// StartingSupplies retains only the first complete native startup census.
// Pending stacks describe an observed need, not ownership or write permission.
type StartingSupplies struct {
	Initialized bool
	Pending     []StartingSupply
}

func (s StartingSupply) Validate() error {
	if _, err := domain.NewSupplyAllow(s.Thing, s.Definition, s.Cell); err != nil {
		return err
	}
	if s.Cell.X >= 4096 || s.Cell.Z >= 4096 {
		return errors.New("invalid starting supply cell")
	}
	return nil
}

func (s StartingSupplies) Validate() error {
	if len(s.Pending) > 256 || !s.Initialized && len(s.Pending) > 0 {
		return errors.New("invalid starting supply history")
	}
	for i, row := range s.Pending {
		if err := row.Validate(); err != nil {
			return err
		}
		if i > 0 && s.Pending[i-1].Thing >= row.Thing {
			return errors.New("unsorted or duplicate starting supply")
		}
	}
	return nil
}

// ReviewStartingSupplies never adopts later forbids or revives released
// stacks; a retained stack takes the cell the census now reports for it. An
// unavailable read preserves history without claiming current recovery.
func ReviewStartingSupplies(observed domain.Fact[[]StartingSupply], previous StartingSupplies) (StartingSupplies, domain.Fact[bool], error) {
	if err := previous.Validate(); err != nil {
		return StartingSupplies{}, domain.Unknown[bool](), err
	}
	previous.Pending = append([]StartingSupply(nil), previous.Pending...)
	rows, known := observed.Value()
	if !known {
		return previous, domain.Unknown[bool](), nil
	}
	rows = append([]StartingSupply(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Thing < rows[j].Thing })
	current := StartingSupplies{Initialized: true, Pending: rows}
	if err := current.Validate(); err != nil {
		return StartingSupplies{}, domain.Unknown[bool](), err
	}
	if !previous.Initialized {
		return current, domain.Known(len(rows) > 0), nil
	}
	seen := map[string]StartingSupply{}
	for _, row := range rows {
		seen[row.Thing] = row
	}
	r := StartingSupplies{Initialized: true}
	for _, row := range previous.Pending {
		if now, ok := seen[row.Thing]; ok {
			r.Pending = append(r.Pending, now)
		}
	}
	return r, domain.Known(len(r.Pending) > 0), nil
}
