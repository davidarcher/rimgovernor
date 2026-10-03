package buildingruntime

import (
	"compress/gzip"
	"io"
	"os"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// fullCatalogRows are the rows of the def classes a fake catalog takes from
// the whole game's recording (observation/testdata/full_catalog.pb.gz): the
// traits and work types a pawn's trait effects read (#1724).
var fullCatalogRows = sync.OnceValues(func() (map[protoreflect.FullName]map[string]proto.Message, error) {
	file, err := os.Open("../observation/testdata/full_catalog.pb.gz")
	if err != nil {
		return nil, err
	}
	defer file.Close()
	zr, err := gzip.NewReader(file)
	if err != nil {
		return nil, err
	}
	data, err := io.ReadAll(zr)
	if err != nil {
		return nil, err
	}
	wire := &o.DefinitionCatalog{}
	if err = proto.Unmarshal(data, wire); err != nil {
		return nil, err
	}
	catalog, err := bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
	if err != nil {
		return nil, err
	}
	out := map[protoreflect.FullName]map[string]proto.Message{}
	for _, class := range []proto.Message{&d.TraitDef{}, &d.WorkTypeDef{}} {
		name := class.ProtoReflect().Descriptor().FullName()
		out[name] = catalog.Defs[name]
	}
	return out, nil
})
