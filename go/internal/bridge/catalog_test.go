package bridge

import (
	"math"
	"testing"

	"google.golang.org/protobuf/proto"
)

// TestDefinitionCatalogRefusesMalformedRows (#1340): the catalog carries
// the planning rows' validation, and refuses another world's catalog.
func TestDefinitionCatalogRefusesMalformedRows(t *testing.T) {
	context := authorityTestContext(7)
	if catalog, err := DecodeDefinitionCatalog(catalogReply(context).GetObserved(), pbIdentity()); err != nil || catalog.Definition("Wall") == nil || len(catalog.Research) != 2 {
		t.Fatalf("%+v %v", catalog, err)
	}
	for _, change := range []string{"world", "duplicate", "cleanliness", "flammability", "path-cost", "research-duplicate", "prerequisite"} {
		t.Run(change, func(t *testing.T) {
			v := catalogReply(context).GetObserved()
			switch change {
			case "world":
				v.Context.Identity.LoadToken = proto.String("other")
			case "duplicate":
				v.Definitions = append(v.Definitions, v.Definitions[0])
			case "cleanliness":
				v.Definitions[0].Cleanliness = proto.Float64(math.NaN())
			case "flammability":
				v.Definitions[0].Flammability = proto.Float64(-1)
			case "path-cost":
				v.Definitions[0].PathCost = proto.Int32(-1)
			case "research-duplicate":
				v.Research = append(v.Research, v.Research[0])
			case "prerequisite":
				v.Research[0].Prerequisites = []string{""}
			}
			if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
				t.Fatal("malformed catalog accepted")
			}
		})
	}
}
