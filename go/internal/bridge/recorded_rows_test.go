package bridge

import (
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

// The recorded catalog (observation/testdata/full_catalog.pb.gz) is the game's
// own rows. A test reads them through sharedRecordedCatalog, or, when it needs a
// row the game does not have (an invented class, a malformed stat, a second
// shell), takes a small slice of them (recordedrows), mutates the slice's copies
// and decodes it with catalogOf.

var sharedRecorded = sync.OnceValues(func() (*DefinitionCatalog, error) {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		return nil, err
	}
	return DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
})

// sharedRecordedCatalog is the decoded recorded catalog, built once per test
// binary. Callers must not mutate it.
func sharedRecordedCatalog(t testing.TB) *DefinitionCatalog {
	t.Helper()
	catalog, err := sharedRecorded()
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

// buildingsSlice is the player-buildable defs and what the joy givers offer.
func buildingsSlice(t testing.TB) *recordedrows.Slice {
	t.Helper()
	return recordedrows.Buildings(t, Buildable)
}

// catalogOf decodes the slice.
func catalogOf(s *recordedrows.Slice) *DefinitionCatalog {
	s.T.Helper()
	catalog, err := DecodeDefinitionCatalog(s.Wire, s.Wire.GetContext().GetIdentity())
	if err != nil {
		s.T.Fatalf("%v", err)
	}
	return catalog
}
