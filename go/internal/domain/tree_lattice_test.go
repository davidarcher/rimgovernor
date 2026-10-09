package domain

import (
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"testing"
)

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
