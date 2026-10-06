package clearance

import "testing"

func TestDumpRequiresNativeStorageAndYard(t *testing.T) {
	for _, fault := range []string{"", "missing_chunk", "unhauled", "wrong_zone", "narrow_filter", "dumping_zone"} {
		t.Run(fault, func(t *testing.T) {
			fixture := map[string]any{"defs": []any{"ChunkGranite", "ChunkSlate"}}
			yard := map[string]any{"label": "Materials yard", "allow": []any{"ChunkGranite", "ChunkSlate", "ChunkSlagSteel", "WoodLog"}}
			chunk := map[string]any{"stored": true, "zone": "Materials yard"}
			live := map[string]any{"zones": []any{yard}, "chunks": []any{chunk, chunk, chunk}}
			switch fault {
			case "missing_chunk":
				live["chunks"] = []any{chunk, chunk}
			case "unhauled":
				chunk["stored"] = false
			case "wrong_zone":
				chunk["zone"] = "Player storage"
			case "narrow_filter":
				yard["allow"] = []any{"ChunkGranite", "ChunkSlate"}
			case "dumping_zone":
				live["zones"] = []any{yard, map[string]any{"label": "Dumping"}}
			}
			err := checkDump(live, fixture)
			if (err != nil) != (fault != "") {
				t.Fatalf("fault %q: %v", fault, err)
			}
		})
	}
}
