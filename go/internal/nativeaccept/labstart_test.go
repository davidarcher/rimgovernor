package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLabCacheName(t *testing.T) {
	defer func() { startCache.expansions = nil }()
	startCache.expansions = nil
	if got := labCacheName(DefaultLabColonist); got != "RimGovernor-lab-100" {
		t.Fatalf("core: %s", got)
	}
	if got := labCacheName(5); got != "RimGovernor-lab-100-c5" {
		t.Fatalf("colonists: %s", got)
	}
	startCache.expansions = []string{"Biotech"}
	if got := labCacheName(DefaultLabColonist); got != "RimGovernor-lab-100-biotech" {
		t.Fatalf("expansion: %s", got)
	}
	if (LabStart{}).colonists() != DefaultLabColonist || (LabStart{Colonists: 2}).colonists() != 2 {
		t.Fatal("colonist default")
	}
	if b := (LabStart{}).base(); b.MapSize != LabMapSize || b.Seed != LabSeed || b.Validate() != nil {
		t.Fatalf("base %+v", b)
	}
}

func TestLabStampInvalidatesOnRebuild(t *testing.T) {
	files := map[string]string{"a.dll": "1", "b.dll": "2"}
	stamp := labStamp(files)
	if labStamp(map[string]string{"b.dll": "2", "a.dll": "1"}) != stamp {
		t.Fatal("stamp depends on map order")
	}
	if labStamp(map[string]string{"a.dll": "1", "b.dll": "3"}) == stamp {
		t.Fatal("a rebuilt file kept the stamp")
	}

	root := t.TempDir()
	defer func(r string, h bool) { startCache.root, startCache.headless = r, h }(startCache.root, startCache.headless)
	startCache.root, startCache.headless = root, false
	name := labCacheName(DefaultLabColonist)
	if fresh, err := labCacheFresh(name, stamp); err != nil || fresh {
		t.Fatalf("no save: %v %v", fresh, err)
	}
	saves := filepath.Join(root, "profile", "Saves")
	if err := os.MkdirAll(saves, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(saves, name+".rws"), []byte("<savegame><meta><modIds><li>ludeon.rimworld</li></modIds></meta></savegame>"), 0o644); err != nil {
		t.Fatal(err)
	}
	if fresh, err := labCacheFresh(name, stamp); err != nil || fresh {
		t.Fatalf("no stamp: %v %v", fresh, err)
	}
	if err := os.WriteFile(labStampPath(name), []byte("other\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if fresh, err := labCacheFresh(name, stamp); err != nil || fresh {
		t.Fatalf("stale stamp: %v %v", fresh, err)
	}
	if err := os.WriteFile(labStampPath(name), []byte(stamp+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if fresh, err := labCacheFresh(name, stamp); err != nil || !fresh {
		t.Fatalf("matching stamp: %v %v", fresh, err)
	}
}
