package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// recordedRows is a slice of the game's own recorded rows (the named defs and
// what they join to), for a test to edit where it needs a row the game does
// not have.
func recordedRows(t testing.TB, names ...string) *recordedrows.Slice {
	t.Helper()
	s := recordedrows.Take(t, recordedrows.Named(), "thing_category_defs")
	s.Add(names...)
	return s
}

// decodeRows decodes a slice as the catalog of a load.
func decodeRows(s *recordedrows.Slice) *bridge.DefinitionCatalog {
	s.T.Helper()
	catalog, err := recordedcatalog.FromSlice(s, "load")
	if err != nil {
		s.T.Fatalf("%v", err)
	}
	return catalog
}

// catalogOf is the catalog of the named recorded defs.
func catalogOf(t testing.TB, names ...string) *bridge.DefinitionCatalog {
	t.Helper()
	return decodeRows(recordedRows(t, names...))
}
