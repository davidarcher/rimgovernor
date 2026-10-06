package domain

import (
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"testing"
)

func TestTreeLatticeCount(t *testing.T) {
	for _, c := range []struct{ w, h, want int }{{5, 4, 6}, {4, 4, 4}, {1, 1, 1}, {3, 3, 4}, {0, 3, 0}} {
		if got := TreeLatticeCount(c.w, c.h); got != c.want {
			t.Errorf("TreeLatticeCount(%d,%d)=%d, want %d", c.w, c.h, got, c.want)
		}
	}
	if TreeCellsPerTree != 4 {
		t.Fatalf("TreeCellsPerTree=%d, want 4", TreeCellsPerTree)
	}
}

// The native sower owns the lattice rule; Go sizes zones from the same pitch.
func TestTreeLatticePitchMatchesNative(t *testing.T) {
	src, err := os.ReadFile(filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "src", "Bridge", "TreeLatticeSowing.cs"))
	if err != nil {
		t.Fatalf("native source: %v", err)
	}
	m := regexp.MustCompile(`const int Pitch = (\d+);`).FindSubmatch(src)
	if m == nil {
		t.Fatal("TreeLatticeSowing.cs declares no Pitch constant")
	}
	if n, _ := strconv.Atoi(string(m[1])); n != TreeLatticePitch {
		t.Fatalf("native Pitch=%d, domain.TreeLatticePitch=%d", n, TreeLatticePitch)
	}
}
