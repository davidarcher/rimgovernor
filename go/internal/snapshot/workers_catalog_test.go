package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// fullCatalog is the whole game's recording (observation/testdata). The
// workers/* recordings predate the work rows' skill and order, which the
// census resolves from the catalog at read time, so loadPawns does
// the same.
var fullCatalog = recordedcatalog.Decode

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
