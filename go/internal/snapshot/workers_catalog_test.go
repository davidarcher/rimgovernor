package snapshot

import (
	"compress/gzip"
	"io"
	"os"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// fullCatalog is the whole game's recording (observation/testdata). The
// workers/* recordings predate the work rows' skill and order, which the
// census resolves from the catalog at read time, so loadPawns does
// the same.
var fullCatalog = sync.OnceValues(func() (*bridge.DefinitionCatalog, error) {
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
	return bridge.DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
})

func resolveWorkRows(t *testing.T, pawns []policy.WorkPawn) {
	t.Helper()
	catalog, err := fullCatalog()
	if err != nil {
		t.Fatal(err)
	}
	for i := range pawns {
		rows, ok := pawns[i].Work.Value()
		if !ok {
			continue
		}
		rows = append([]policy.WorkPriority(nil), rows...)
		for j := range rows {
			if err = catalog.ResolveWorkRow(&rows[j]); err != nil {
				t.Fatal(err)
			}
		}
		pawns[i].Work = domain.Known(rows)
	}
}
