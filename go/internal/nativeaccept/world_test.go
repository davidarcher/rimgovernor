package nativeaccept

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestRecordWorldReadsTheSaveSeedAndHashesTheFixture(t *testing.T) {
	root := t.TempDir()
	save := []byte("<savegame><meta><modIds><li>ludeon.rimworld</li></modIds></meta><game><world><info><seedString>  Tangent  </seedString></info></world></game></savegame>")
	dir := filepath.Join(root, "headless-profile", "Saves")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "world.rws"), save, 0644); err != nil {
		t.Fatal(err)
	}
	cfg := &Config{Root: root, Headless: true}
	sum := sha256.Sum256(save)
	rec := RecordWorld(cfg, Fixture{Op: "test/x_prepare", Args: map[string]any{"b": 2, "a": 1}, On: Save{Name: "world"}})
	want := WorldRecord{Seed: "Tangent", Save: "world", SaveSHA256: hex.EncodeToString(sum[:]), Fixture: "test/x_prepare", FixtureHash: FixtureHash("test/x_prepare", map[string]any{"a": 1, "b": 2})}
	if rec != want {
		t.Errorf("record = %+v, want %+v", rec, want)
	}
	if rec.FixtureHash == "" || rec.FixtureHash == FixtureHash("test/x_prepare", nil) {
		t.Errorf("fixture hash %q does not cover the arguments", rec.FixtureHash)
	}
	// A save that is not there leaves what the start knew.
	if rec := RecordWorld(cfg, Save{Name: "missing"}); rec != (WorldRecord{Save: "missing"}) {
		t.Errorf("missing save = %+v", rec)
	}
	// A scenario start knows its own seed and pins it.
	if rec := RecordWorld(cfg, ScenarioStart{Seed: "tribal-e"}); rec != (WorldRecord{Seed: "tribal-e", Pinned: true}) {
		t.Errorf("scenario = %+v", rec)
	}
	// The record round-trips through a JSON report.
	data, _ := json.Marshal(Report{"world": rec})
	var decoded map[string]any
	_ = json.Unmarshal(data, &decoded)
	if got, ok := WorldOf(decoded); !ok || got != rec {
		t.Errorf("WorldOf = %+v, %v", got, ok)
	}
	if _, ok := WorldOf(map[string]any{}); ok {
		t.Error("WorldOf found a block in an empty report")
	}
}

func TestDebugStartWorldFollowsTheCacheAndTheDrawnSeed(t *testing.T) {
	root := t.TempDir()
	t.Setenv(CachedStartEnv, "")
	startCache.root, startCache.headless, startCache.expansions, startCache.seed = root, true, nil, "drawnseed"
	t.Cleanup(func() { startCache.root, startCache.headless, startCache.seed = "", false, "" })
	// No cached save yet: the seed the start drew is all the record has.
	if rec := RecordWorld(&Config{Root: root, Headless: true}, DebugStart{}); rec != (WorldRecord{Seed: "drawnseed"}) {
		t.Errorf("uncached = %+v", rec)
	}
	name := cachedStartName(DefaultDebugStart())
	dir := filepath.Join(root, "headless-profile", "Saves")
	_ = os.MkdirAll(dir, 0755)
	_ = os.WriteFile(filepath.Join(dir, name+".rws"), []byte("<seedString>cached</seedString>"), 0644)
	// The cached save's seed wins once it is there, and a pinned seed is
	// recorded as pinned under its own save.
	rec := RecordWorld(&Config{Root: root, Headless: true}, DebugStart{})
	if rec.Save != name || rec.Seed != "cached" || rec.Pinned || rec.SaveSHA256 == "" {
		t.Errorf("cached = %+v", rec)
	}
	if rec := RecordWorld(&Config{Root: root, Headless: true}, DebugStart{Seed: "Pin-1"}); rec.Save != "" || !rec.Pinned {
		t.Errorf("pinned = %+v", rec)
	}
	if got := cachedStartName(DebugStart{MapSize: 200, PlanetCoverage: 0.05, Seed: "Pin-1"}); got != "RimGovernor-debug-200-0_05-seed-pin1" {
		t.Errorf("pinned name = %q", got)
	}
}

func TestRandomSeedIsASaveNameToken(t *testing.T) {
	a, b := RandomSeed(), RandomSeed()
	if len(a) != 10 || a == b || seedToken(a) != a {
		t.Errorf("seeds %q %q", a, b)
	}
}
