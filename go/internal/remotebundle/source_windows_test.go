package remotebundle

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestMaterializeWindowsJunction(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join(root, "source")
	link := filepath.Join(root, "junction")
	dest := filepath.Join(root, "copy")
	if err := os.Mkdir(source, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(source, "file"), []byte("original"), 0600); err != nil {
		t.Fatal(err)
	}
	if out, err := exec.Command("cmd", "/c", "mklink", "/J", link, source).CombinedOutput(); err != nil {
		t.Fatalf("junction: %s %v", out, err)
	}
	// Remove the link before TempDir cleanup; never recurse into its target.
	defer os.Remove(link)
	if err := Materialize(link, dest); err != nil {
		t.Fatal(err)
	}
	inv, err := InventoryTree(source)
	if err != nil {
		t.Fatal(err)
	}
	if err := VerifyTree(dest, inv); err != nil {
		t.Fatal(err)
	}
	if err := Materialize(link, filepath.Join(link, "recursive")); err == nil {
		t.Fatal("destination through source junction accepted")
	}
}
