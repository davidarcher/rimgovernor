package clearance

import "testing"

func TestDumpRequiresNativeStorageAndExactZone(t *testing.T) {
	for _, fault := range []string{"", "missing_chunk", "unhauled", "wrong_zone", "extra_filter", "high_priority", "roofed", "outside_home", "duplicate_zone"} {
		t.Run(fault, func(t *testing.T) {
			fixture := map[string]any{"defs": []any{"ChunkGranite", "ChunkSlate", "ChunkSlagSteel"}}
			cell := map[string]any{"home": true, "roofed": false, "building": false}
			zone := map[string]any{"label": "RimGovernor dumping", "priority": "Low", "allow": fixture["defs"], "cells": []any{cell, cell, cell, cell}}
			chunk := map[string]any{"stored": true, "zone": "RimGovernor dumping"}
			live := map[string]any{"zones": []any{zone}, "chunks": []any{chunk, chunk, chunk}}
			switch fault {
			case "missing_chunk":
				live["chunks"] = []any{chunk, chunk}
			case "unhauled":
				chunk["stored"] = false
			case "wrong_zone":
				chunk["zone"] = "Player storage"
			case "extra_filter":
				zone["allow"] = []any{"ChunkGranite", "ChunkSlate", "ChunkSlagSteel", "WoodLog"}
			case "high_priority":
				zone["priority"] = "Normal"
			case "roofed":
				cell["roofed"] = true
			case "outside_home":
				cell["home"] = false
			case "duplicate_zone":
				live["zones"] = []any{zone, zone}
			}
			err := checkDump(live, fixture)
			if (err != nil) != (fault != "") {
				t.Fatalf("fault %q: %v", fault, err)
			}
		})
	}
}
