package remotebundle

import (
	"os"
	"path/filepath"
	"slices"
	"testing"
)

func TestGamePathsFlat17Layout(t *testing.T) {
	dir := t.TempDir()
	for _, f := range []string{"RimWorld.exe", "Waylith.dll", "Assembly-CSharp.dll", "Assets.zip", "Version.txt", "System.Runtime.dll", "glfw3.pdb"} {
		if err := os.WriteFile(filepath.Join(dir, f), []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	for _, d := range []string{"Mods", "Mods.disabled", "RimWorldWin64_Data", "NVIDIA Corporation", "Data"} {
		if err := os.MkdirAll(filepath.Join(dir, d), 0o700); err != nil {
			t.Fatal(err)
		}
	}
	got, err := GamePaths(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"RimWorld.exe", "Assets.zip", "System.Runtime.dll", "Rendering", "Data/Core", "Data/Odyssey"} {
		if !slices.Contains(got, want) {
			t.Errorf("missing %s in %v", want, got)
		}
	}
	for _, bad := range []string{"glfw3.pdb", "Mods", "Mods.disabled", "RimWorldWin64_Data", "NVIDIA Corporation", "Data"} {
		if slices.Contains(got, bad) {
			t.Errorf("%s must not ship", bad)
		}
	}
	if err := os.Remove(filepath.Join(dir, "Assets.zip")); err != nil {
		t.Fatal(err)
	}
	if _, err := GamePaths(dir); err == nil {
		t.Fatal("an install without Assets.zip must be refused")
	}
}
