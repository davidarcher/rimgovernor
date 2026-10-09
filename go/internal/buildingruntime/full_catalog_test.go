package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// fullCatalogRows are the rows of the def classes a fake catalog takes from
// the whole game's recording (observation/testdata/full_catalog.pb.gz): the
// traits, thoughts and work types a pawn's trait effects and work rows read.
var fullCatalogRows = sync.OnceValues(func() (map[protoreflect.FullName]map[string]proto.Message, error) {
	catalog, err := recordedcatalog.Decode()
	if err != nil {
		return nil, err
	}
	out := map[protoreflect.FullName]map[string]proto.Message{}
	for _, class := range []proto.Message{&d.TraitDef{}, &d.WorkTypeDef{}, &d.ThoughtDef{}, &d.NeedDef{}} {
		name := class.ProtoReflect().Descriptor().FullName()
		out[name] = catalog.Defs[name]
	}
	return out, nil
})
