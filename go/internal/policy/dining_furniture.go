package policy

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DiningFurniture is the furniture the dining and recreation planners place,
// as the catalog rows name it (DefinitionCatalog.DiningFurniture): the chair
// and the table the dining template lays out, the recreation foothold it
// puts against the back wall, and how many cells of floor the foothold's
// throw lane takes (the pin cell and the cells a thrower stands on toward the
// entrance). It is a threaded value on the observation, never a global.
type DiningFurniture struct {
	Chair, Table, Pin InteriorPieceDef
	Lane              int32
}

// Known reports whether the catalog filled the value in.
func (f DiningFurniture) Known() bool {
	return f.Chair.Def != "" && f.Table.Def != "" && f.Pin.Def != ""
}

// IsChair reports whether the definition is the dining chair.
func (f DiningFurniture) IsChair(def string) bool { return def != "" && def == f.Chair.Def }

// IsTable reports whether the definition is the dining table.
func (f DiningFurniture) IsTable(def string) bool { return def != "" && def == f.Table.Def }

// tableMethod and chairMethod are the comfort methods that build the table and
// the chair; a census without them cannot name the build.
func (f DiningFurniture) tableMethod() (ComfortMethod, error) {
	if f.Table.Def == "" {
		return "", errors.New("comfort census names no dining table")
	}
	return ComfortMethod(f.Table.Def), nil
}

func (f DiningFurniture) chairMethod() (ComfortMethod, error) {
	if f.Chair.Def == "" {
		return "", errors.New("comfort census names no dining chair")
	}
	return ComfortMethod(f.Chair.Def), nil
}

// Definitions are the chair, the table and the foothold, in that order.
func (f DiningFurniture) Definitions() []string {
	return []string{f.Table.Def, f.Chair.Def, f.Pin.Def}
}

// Validate checks the shapes the dining template lays out: a one-cell chair
// and pin, a table one cell wide and two deep, and a lane that holds the pin.
func (f DiningFurniture) Validate() error {
	if !f.Known() {
		return errors.New("dining furniture names no chair, table or recreation foothold")
	}
	cell, table := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 1, Z: 2}
	for _, p := range []struct {
		role string
		def  InteriorPieceDef
		size domain.Cell
	}{{"chair", f.Chair, cell}, {"table", f.Table, table}, {"recreation foothold", f.Pin, cell}} {
		if p.def.Size != p.size {
			return fmt.Errorf("dining %s %s is %dx%d, the dining template lays out %dx%d", p.role, p.def.Def, p.def.Size.X, p.def.Size.Z, p.size.X, p.size.Z)
		}
	}
	if f.Lane < 1 {
		return fmt.Errorf("dining recreation foothold %s has a lane of %d cells", f.Pin.Def, f.Lane)
	}
	return nil
}
