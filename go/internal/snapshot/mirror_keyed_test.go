package snapshot

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestRecordKeyedMatchesRows (#1578): a section published as a persistent
// table version records the same lines as the same rows published as a
// map: keyframe, upserts, removals, unchanged tables and a scope change.
func TestRecordKeyedMatchesRows(t *testing.T) {
	row := func(id string, kind string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, KindDefName: proto.String(kind)}
	}
	rowsDir, keyedDir := t.TempDir(), t.TempDir()
	rowsStore, keyedStore := facts.NewStore(), facts.NewStore()
	rowsStore.SetRecorder(MirrorRecorder(rowsDir))
	keyedStore.SetRecorder(MirrorRecorder(keyedDir))
	scope := facts.Scope{Load: "l", Map: 1, Generation: 1}
	rows := map[string]*o.PawnState{}
	var table bridge.Table[*o.PawnState]
	for i := 0; i < 30; i++ {
		switch i % 6 {
		case 0, 3:
			id := string(rune('a' + i%20))
			r := row(id, "kind")
			rows[id], table = r, table.Set(id, r)
		case 1:
			for id := range rows {
				delete(rows, id)
				table = table.Delete(id)
				break
			}
		case 2:
			// A new row object with the same content records nothing.
			for id, old := range rows {
				r := proto.Clone(old).(*o.PawnState)
				rows[id], table = r, table.Set(id, r)
				break
			}
		case 4:
			scope.Generation++
		}
		// Rows are not copied by the store; hand each store its own map.
		cp := make(map[string]*o.PawnState, len(rows))
		for k, v := range rows {
			cp[k] = v
		}
		facts.PutTable(rowsStore, scope, "pawns", cp, facts.At(int64(100+i)))
		facts.PutKeyed(keyedStore, scope, "pawns", table, facts.At(int64(100+i)))
	}
	read := func(dir string) []byte {
		files, _ := filepath.Glob(filepath.Join(dir, "routine-stream-*.jsonl"))
		if len(files) != 1 {
			t.Fatalf("%s holds %d streams", dir, len(files))
		}
		data, err := os.ReadFile(files[0])
		if err != nil {
			t.Fatal(err)
		}
		return data
	}
	if want, got := read(rowsDir), read(keyedDir); string(want) != string(got) {
		t.Fatalf("keyed recording differs from the row recording:\nrows:\n%s\nkeyed:\n%s", want, got)
	}
}
