package wall

import "testing"

func siteRow(nx, nz float64, backups int) map[string]any {
	building := func(id string) map[string]any { return map[string]any{"id": id} }
	var cells []any
	for i := 0; i < backups; i++ {
		cells = append(cells, map[string]any{"x": float64(i), "z": 0.0})
	}
	return map[string]any{
		"target":         map[string]any{"id": "W"},
		"targetSnapshot": map[string]any{"token": "t"},
		"original":       building("W"),
		"targetPresent":  true,
		"normal":         map[string]any{"x": nx, "z": nz},
		"leftSupport":    building("L"),
		"rightSupport":   building("R"),
		"backupCells":    cells,
		"replacementMaterials": []any{map[string]any{"stuff": "BlocksGranite",
			"costs": []any{map[string]any{"defName": "BlocksGranite", "units": 5.0}}}},
	}
}

// A corner site has no backup cells; a straight one has three (CI run
// 36957589404 failed wall/upgrade on a corner row).
func TestCheckSitesBackupCellsByNormal(t *testing.T) {
	for _, c := range []struct {
		name    string
		row     map[string]any
		wantErr bool
	}{
		{"corner", siteRow(1, -1, 0), false},
		{"straight", siteRow(1, 0, 3), false},
		{"straight without backups", siteRow(0, 1, 0), true},
		{"corner with backups", siteRow(1, 1, 3), true},
	} {
		if err := checkSites("W", []any{c.row}); (err != nil) != c.wantErr {
			t.Errorf("%s: err = %v", c.name, err)
		}
	}
}
