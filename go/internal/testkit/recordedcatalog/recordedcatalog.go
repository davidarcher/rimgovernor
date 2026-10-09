// Package recordedcatalog decodes the recorded whole-game catalog for tests
// above the bridge layer (testkit itself cannot import bridge, whose own
// tests use testkit).
package recordedcatalog

import (
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Decode is the decoded catalog, built once per test binary and shared:
// callers must not mutate it.
var Decode = sync.OnceValues(func() (*bridge.DefinitionCatalog, error) {
	wire, err := testkit.LoadRecordedCatalogWire()
	if err != nil {
		return nil, err
	}
	return bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
})

// Catalog is the decoded recorded catalog.
func Catalog(t testing.TB) *bridge.DefinitionCatalog {
	t.Helper()
	catalog, err := Decode()
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

// Wire is the recorded catalog message.
func Wire(t testing.TB) *o.DefinitionCatalog { return testkit.RecordedCatalogWire(t) }
