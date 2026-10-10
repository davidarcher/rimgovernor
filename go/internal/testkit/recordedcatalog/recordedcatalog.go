// Package recordedcatalog decodes the recorded whole-game catalog for tests
// above the bridge layer (testkit itself cannot import bridge, whose own
// tests use testkit).
package recordedcatalog

import (
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/recordedrows"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// FromSlice decodes a slice of recorded rows as the catalog of one load (colony
// "colony", map 0), the identity the fakes of the planner tests serve under.
func FromSlice(s *recordedrows.Slice, loadToken string) (*bridge.DefinitionCatalog, error) {
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String(loadToken), MapId: proto.Int32(0)}
	s.Wire.Context = &c.ObservationContext{Identity: identity, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)}
	return bridge.DecodeDefinitionCatalog(s.Wire, identity)
}

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
