package bridge

import (
	"testing"

	"google.golang.org/protobuf/proto"
)

// TestDefinitionCatalogRefusesMalformedRows (#1340): the catalog refuses
// another world's catalog and malformed research rows.
func TestDefinitionCatalogRefusesMalformedRows(t *testing.T) {
	context := authorityTestContext(7)
	if catalog, err := DecodeDefinitionCatalog(catalogReply(context).GetObserved(), pbIdentity()); err != nil || catalog.ThingDef("Wall") == nil || len(catalog.Research) != 2 {
		t.Fatalf("%+v %v", catalog, err)
	}
	for _, change := range []string{"world", "research-duplicate", "prerequisite"} {
		t.Run(change, func(t *testing.T) {
			v := catalogReply(context).GetObserved()
			switch change {
			case "world":
				v.Context.Identity.LoadToken = proto.String("other")
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
