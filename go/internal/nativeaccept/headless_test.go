package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func writeAbout(t *testing.T, path, packageID string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		t.Fatal(err)
	}
	xml := "<?xml version='1.0' encoding='utf8'?>\n<ModMetaData>\n  <packageId>" + packageID + "</packageId>\n</ModMetaData>"
	if err := os.WriteFile(path, []byte(xml), 0644); err != nil {
		t.Fatal(err)
	}
}

func writeUnifiedPackage(t *testing.T, mods string) {
	t.Helper()
	pkg := filepath.Join(mods, "RimGovernor")
	writeAbout(t, filepath.Join(pkg, "About", "About.xml"), NativePackage)
	for _, relative := range []string{
		filepath.Join("Assemblies", "RimGovernor.Runtime.dll"),
		filepath.Join("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"),
	} {
		full := filepath.Join(pkg, relative)
		if err := os.MkdirAll(filepath.Dir(full), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(full, []byte("stub"), 0644); err != nil {
			t.Fatal(err)
		}
	}
}

func TestRequireNativePackageAcceptsUnifiedPackage(t *testing.T) {
	mods := t.TempDir()
	writeUnifiedPackage(t, mods)
	if err := RequireNativePackage(mods); err != nil {
		t.Fatalf("expected unified package to be accepted: %v", err)
	}
}

func TestRequireNativePackageRejectsMissingFile(t *testing.T) {
	mods := t.TempDir()
	writeAbout(t, filepath.Join(mods, "RimGovernor", "About", "About.xml"), NativePackage)
	if err := RequireNativePackage(mods); err == nil {
		t.Fatal("expected error for missing assemblies")
	}
}

func TestRequireNativePackageRejectsLegacySplitPackage(t *testing.T) {
	mods := t.TempDir()
	writeUnifiedPackage(t, mods)
	writeAbout(t, filepath.Join(mods, "RimGovernorObservations", "About", "About.xml"), "davidarcher.rimgovernor.observations")
	if err := RequireNativePackage(mods); err == nil {
		t.Fatal("expected error for legacy split package present")
	}
}

func TestRequireNativePackageRejectsWrongIdentity(t *testing.T) {
	mods := t.TempDir()
	writeUnifiedPackage(t, mods)
	writeAbout(t, filepath.Join(mods, "RimGovernor", "About", "About.xml"), "someone.else.mod")
	if err := RequireNativePackage(mods); err == nil {
		t.Fatal("expected error for mismatched package identity")
	}
}

func TestRequireNativePackageRejectsDuplicateUnified(t *testing.T) {
	mods := t.TempDir()
	writeUnifiedPackage(t, mods)
	writeAbout(t, filepath.Join(mods, "AnotherCopy", "About", "About.xml"), NativePackage)
	if err := RequireNativePackage(mods); err == nil {
		t.Fatal("expected error for duplicate unified package")
	}
}

const sampleModsConfig = `<?xml version='1.0' encoding='utf8'?>
<ModsConfigData>
  <version>1.6.4871 rev591</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
    <li>davidarcher.rimgovernor.observations</li>
    <li>brrainz.rimbridgeserver</li>
  </activeMods>
  <knownExpansions>
    <li>ludeon.rimworld.royalty</li>
  </knownExpansions>
</ModsConfigData>`

func TestPrepareNativeModConfig(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "ModsConfig.xml")
	if err := os.WriteFile(path, []byte(sampleModsConfig), 0644); err != nil {
		t.Fatal(err)
	}
	if err := PrepareNativeModConfig(path, nil); err != nil {
		t.Fatalf("PrepareNativeModConfig failed: %v", err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	_, root, err := parseXML(data)
	if err != nil {
		t.Fatalf("rewritten ModsConfig.xml did not parse: %v", err)
	}
	active := root.find("activeMods")
	if active == nil {
		t.Fatal("activeMods missing after rewrite")
	}
	got := active.li()
	want := []string{"ludeon.rimworld", "brrainz.harmony", "brrainz.rimbridgeserver", NativePackage}
	if len(got) != len(want) {
		t.Fatalf("activeMods = %v, want %v", got, want)
	}
	for _, id := range []string{"brrainz.harmony", "brrainz.rimbridgeserver", NativePackage} {
		found := false
		for _, g := range got {
			if g == id {
				found = true
			}
		}
		if !found {
			t.Fatalf("activeMods missing required entry %s: %v", id, got)
		}
	}
	for _, g := range got {
		if g == "davidarcher.rimgovernor.observations" {
			t.Fatalf("legacy package was not removed: %v", got)
		}
	}
	// knownExpansions and version must be untouched.
	if root.childText("version") != "1.6.4871 rev591" {
		t.Fatalf("version element was mutated: %q", root.childText("version"))
	}
	if len(root.find("knownExpansions").li()) != 1 {
		t.Fatalf("knownExpansions element was mutated")
	}
}

const expansionModsConfig = `<?xml version='1.0' encoding='utf8'?>
<ModsConfigData>
  <version>1.6.4871 rev591</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.odyssey</li>
    <li>redeyedev.rimapi</li>
  </activeMods>
  <knownExpansions>
    <li>ludeon.rimworld.royalty</li>
    <li>ludeon.rimworld.ideology</li>
    <li>ludeon.rimworld.biotech</li>
    <li>ludeon.rimworld.odyssey</li>
  </knownExpansions>
</ModsConfigData>`

func activeModsAfter(t *testing.T, expansions ...string) []string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "ModsConfig.xml")
	if err := os.WriteFile(path, []byte(expansionModsConfig), 0644); err != nil {
		t.Fatal(err)
	}
	if err := PrepareNativeModConfig(path, nil, expansions...); err != nil {
		t.Fatalf("PrepareNativeModConfig failed: %v", err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	_, root, err := parseXML(data)
	if err != nil {
		t.Fatalf("rewritten ModsConfig.xml did not parse: %v", err)
	}
	// Every requested expansion is known afterwards, so RimWorld does not
	// treat it as newly installed; the profile's own entries stay (#332).
	known := root.find("knownExpansions").li()
	if len(known) < 4 || strings.Join(known[:4], ",") != "ludeon.rimworld.royalty,ludeon.rimworld.ideology,ludeon.rimworld.biotech,ludeon.rimworld.odyssey" {
		t.Fatalf("knownExpansions = %v, want the profile's four first", known)
	}
	for _, id := range expansions {
		want, _ := ExpansionPackage(id)
		found := false
		for _, k := range known {
			found = found || k == want
		}
		if !found {
			t.Fatalf("knownExpansions = %v, missing requested %s", known, want)
		}
	}
	return root.find("activeMods").li()
}

func TestPrepareNativeModConfigKnowsInstalledExpansions(t *testing.T) {
	// A game copy with every DLC installed: each one lands in
	// knownExpansions (once, casefolded, the profile's own entries first)
	// while activeMods stays Core-only, so RimWorld's boot-time "newly
	// installed expansion" activation never fires (#332). A missing
	// knownExpansions element is created.
	path := filepath.Join(t.TempDir(), "ModsConfig.xml")
	if err := os.WriteFile(path, []byte("<ModsConfigData><activeMods><li>ludeon.rimworld</li><li>Ludeon.RimWorld.Royalty</li></activeMods></ModsConfigData>"), 0644); err != nil {
		t.Fatal(err)
	}
	installed := []string{"ludeon.rimworld.anomaly", "ludeon.rimworld.biotech", "Ludeon.RimWorld.Royalty", "ludeon.rimworld.royalty"}
	if err := PrepareNativeModConfig(path, installed); err != nil {
		t.Fatalf("PrepareNativeModConfig failed: %v", err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	_, root, err := parseXML(data)
	if err != nil {
		t.Fatalf("rewritten ModsConfig.xml did not parse: %v", err)
	}
	if got := strings.Join(root.find("activeMods").li(), ","); got != "ludeon.rimworld,brrainz.harmony,brrainz.rimbridgeserver,"+NativePackage {
		t.Fatalf("activeMods = %s", got)
	}
	if got := strings.Join(root.find("knownExpansions").li(), ","); got != "ludeon.rimworld.anomaly,ludeon.rimworld.biotech,ludeon.rimworld.royalty" {
		t.Fatalf("knownExpansions = %s", got)
	}
}

func TestInstalledExpansions(t *testing.T) {
	game := t.TempDir()
	for name, id := range map[string]string{"Core": "Ludeon.RimWorld", "Royalty": "Ludeon.RimWorld.Royalty", "Anomaly": "ludeon.rimworld.anomaly"} {
		dir := filepath.Join(game, "Data", name, "About")
		if err := os.MkdirAll(dir, 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, "About.xml"), []byte("<ModMetaData><packageId>"+id+"</packageId></ModMetaData>"), 0644); err != nil {
			t.Fatal(err)
		}
	}
	got, err := InstalledExpansions(game)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(got, ",") != "ludeon.rimworld.anomaly,ludeon.rimworld.royalty" {
		t.Fatalf("InstalledExpansions = %v", got)
	}
	if got, err := InstalledExpansions(filepath.Join(game, "missing")); err != nil || len(got) != 0 {
		t.Fatalf("InstalledExpansions(no Data) = %v, %v", got, err)
	}
}

func TestPrepareNativeModConfigDropsExpansionsByDefault(t *testing.T) {
	got := activeModsAfter(t)
	want := []string{"ludeon.rimworld", "redeyedev.rimapi", "brrainz.harmony", "brrainz.rimbridgeserver", NativePackage}
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("activeMods = %v, want %v", got, want)
	}
}

func TestPrepareNativeModConfigKeepsRequestedExpansions(t *testing.T) {
	// Short names and full IDs both work, duplicates collapse, and the kept
	// expansions load directly after the core game in request order even when
	// the profile had them inactive.
	got := activeModsAfter(t, "biotech", "ludeon.rimworld.royalty", "Biotech", "anomaly")
	want := []string{"ludeon.rimworld", "ludeon.rimworld.biotech", "ludeon.rimworld.royalty", "ludeon.rimworld.anomaly", "redeyedev.rimapi", "brrainz.harmony", "brrainz.rimbridgeserver", NativePackage}
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("activeMods = %v, want %v", got, want)
	}
}

func TestPrepareNativeModConfigRejectsBadExpansion(t *testing.T) {
	for _, bad := range []string{"", "ludeon.rimworld", "royalty.extra", "roy alty"} {
		path := filepath.Join(t.TempDir(), "ModsConfig.xml")
		if err := os.WriteFile(path, []byte(expansionModsConfig), 0644); err != nil {
			t.Fatal(err)
		}
		if err := PrepareNativeModConfig(path, nil, bad); err == nil {
			t.Fatalf("expected error for expansion %q", bad)
		}
	}
}

func TestPrepareNativeModConfigRequiresCore(t *testing.T) {
	path := filepath.Join(t.TempDir(), "ModsConfig.xml")
	if err := os.WriteFile(path, []byte("<ModsConfigData><activeMods><li>brrainz.harmony</li></activeMods></ModsConfigData>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := PrepareNativeModConfig(path, nil); err == nil {
		t.Fatal("expected error when ludeon.rimworld is inactive")
	}
}

func TestExpansionsFromEnv(t *testing.T) {
	t.Setenv(ExpansionsEnv, "")
	if got, err := ExpansionsFromEnv(); err != nil || got != nil {
		t.Fatalf("empty env: %v, %v", got, err)
	}
	t.Setenv(ExpansionsEnv, " royalty, ludeon.rimworld.biotech ,")
	got, err := ExpansionsFromEnv()
	if err != nil || strings.Join(got, ",") != "ludeon.rimworld.royalty,ludeon.rimworld.biotech" {
		t.Fatalf("env parse: %v, %v", got, err)
	}
	t.Setenv(ExpansionsEnv, "ludeon.rimworld")
	if _, err := ExpansionsFromEnv(); err == nil {
		t.Fatal("expected error for the core package")
	}
}

func TestPrepareNativeModConfigMissingActiveMods(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "ModsConfig.xml")
	if err := os.WriteFile(path, []byte("<ModsConfigData><version>1</version></ModsConfigData>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := PrepareNativeModConfig(path, nil); err == nil {
		t.Fatal("expected error for missing activeMods")
	}
}

// withoutCommittedSaves keeps a test's roots hermetic: Prepare must not
// stage the real checkout's committed baseline over the test's own.
func withoutCommittedSaves(t *testing.T) {
	t.Helper()
	previous := findCommittedSaves
	findCommittedSaves = func() (string, bool) { return "", false }
	t.Cleanup(func() { findCommittedSaves = previous })
}

// withCommittedSaves points staging at dir as the checkout's committed
// saves directory.
func withCommittedSaves(t *testing.T, dir string) {
	t.Helper()
	previous := findCommittedSaves
	findCommittedSaves = func() (string, bool) { return dir, true }
	t.Cleanup(func() { findCommittedSaves = previous })
}

func writeSourceRoot(t *testing.T) string {
	t.Helper()
	withoutCommittedSaves(t)
	source := filepath.Join(t.TempDir(), "source")
	mods := filepath.Join(source, "install", "Mods")
	writeUnifiedPackage(t, mods)
	config := map[string]any{
		"version": "1.0",
		"games": map[string]any{
			"rimgovernor-trial": map[string]any{
				"id":              "rimgovernor-trial",
				"launchMode":      "DirectPath",
				"target":          filepath.Join(source, "install", "Game.exe"),
				"workingDir":      filepath.Join(source, "install"),
				"stopProcessName": "Game.exe",
			},
		},
	}
	if err := os.MkdirAll(filepath.Join(source, "config"), 0755); err != nil {
		t.Fatal(err)
	}
	data, _ := json.MarshalIndent(config, "", "  ")
	if err := os.WriteFile(filepath.Join(source, "config", "config.json"), data, 0644); err != nil {
		t.Fatal(err)
	}
	gabsDir := filepath.Join(source, "gabs", "gabs-v1.1.1-windows-amd64")
	if err := os.MkdirAll(gabsDir, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(gabsDir, "gabs.exe"), []byte("stub-binary"), 0644); err != nil {
		t.Fatal(err)
	}
	profile := filepath.Join(source, "profile")
	if err := os.MkdirAll(filepath.Join(profile, "Config"), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(profile, "Saves"), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(profile, "Config", "Prefs.xml"), []byte("<PrefsData>\n  <autosaveIntervalDays>1</autosaveIntervalDays>\n</PrefsData>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(profile, "Config", "ModsConfig.xml"), []byte(sampleModsConfig), 0644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(profile, "Saves", "RimGovernor-tribal8-baseline.rws"), []byte("save-data"), 0644); err != nil {
		t.Fatal(err)
	}
	return source
}

func TestIsolatedRootBuildsFreshWorker(t *testing.T) {
	source := writeSourceRoot(t)
	destination := filepath.Join(t.TempDir(), "worker")
	got, err := IsolatedRoot(source, destination)
	if err != nil {
		t.Fatalf("IsolatedRoot failed: %v", err)
	}
	if got != destination {
		t.Fatalf("IsolatedRoot returned %q, want %q", got, destination)
	}
	for _, relative := range []string{
		filepath.Join("gabs", "gabs-v1.1.1-windows-amd64", "gabs.exe"),
		filepath.Join("config", "config.json"),
		filepath.Join("profile", "Config", "Prefs.xml"),
		filepath.Join("profile", "Config", "ModsConfig.xml"),
		filepath.Join("profile", "Saves", "RimGovernor-tribal8-baseline.rws"),
	} {
		if _, err := os.Stat(filepath.Join(destination, relative)); err != nil {
			t.Fatalf("expected %s to exist: %v", relative, err)
		}
	}
	config, err := loadConfig(filepath.Join(destination, "config", "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	game, err := gameSection(config)
	if err != nil {
		t.Fatal(err)
	}
	if _, present := game["stopProcessName"]; present {
		t.Fatal("stopProcessName should have been stripped")
	}
	section, _ := config["rimgovernor"].(map[string]any)
	if section == nil || section["gabsExecutable"] != "gabs/gabs-v1.1.1-windows-amd64/gabs.exe" {
		t.Fatalf("gabsExecutable not rewritten to a relative path: %v", section)
	}
}

func TestIsolatedRootRefusesExistingDestination(t *testing.T) {
	source := writeSourceRoot(t)
	destination := t.TempDir() // already exists
	if _, err := IsolatedRoot(source, destination); err == nil {
		t.Fatal("expected error when destination already exists")
	}
}

func TestIsolatedRootRejectsNonDirectPath(t *testing.T) {
	source := writeSourceRoot(t)
	config, err := loadConfig(filepath.Join(source, "config", "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	game, _ := gameSection(config)
	game["launchMode"] = "SteamAppId"
	if err := writeConfig(filepath.Join(source, "config", "config.json"), config); err != nil {
		t.Fatal(err)
	}
	if _, err := IsolatedRoot(source, filepath.Join(t.TempDir(), "worker")); err == nil {
		t.Fatal("expected error for non-DirectPath launch mode")
	}
}

func TestPrepareRewritesHeadlessArgs(t *testing.T) {
	source := writeSourceRoot(t)
	destination := filepath.Join(t.TempDir(), "worker")
	root, err := IsolatedRoot(source, destination)
	if err != nil {
		t.Fatal(err)
	}
	configuration, err := Prepare(root)
	if err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	if configuration != filepath.Join(root, "config-headless") {
		t.Fatalf("Prepare returned %q", configuration)
	}
	config, err := loadConfig(filepath.Join(configuration, "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	game, err := gameSection(config)
	if err != nil {
		t.Fatal(err)
	}
	args, ok := game["args"].([]any)
	if !ok {
		t.Fatalf("args missing or wrong type: %v", game["args"])
	}
	joined := make([]string, len(args))
	for i, a := range args {
		joined[i] = a.(string)
	}
	foundBatch, foundNoGraphics, foundAcceleration := false, false, false
	for _, a := range joined {
		if a == "-batchmode" {
			foundBatch = true
		}
		if a == "-nographics" {
			foundNoGraphics = true
		}
		if a == "-rimgovernor-test-acceleration" {
			foundAcceleration = true
		}
	}
	if !foundBatch || !foundNoGraphics {
		t.Fatalf("headless args missing batch/nographics flags: %v", joined)
	}
	if !foundAcceleration {
		t.Fatalf("headless args missing the test-acceleration gate: %v", joined)
	}
	if _, err := os.Stat(filepath.Join(root, "headless-profile", "Config", "ModsConfig.xml")); err != nil {
		t.Fatalf("headless-profile ModsConfig.xml missing: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "headless-profile", "Saves", "RimGovernor-tribal8-baseline.rws")); err != nil {
		t.Fatalf("headless-profile baseline save missing: %v", err)
	}
}

// Prepare runs on every harness start, including one attaching to a kept
// process whose journal cursor lives in native static state, so it must
// leave the journal alone; only a fresh launch clears it (#119).
func TestPrepareKeepsClockJournal(t *testing.T) {
	source := writeSourceRoot(t)
	root, err := IsolatedRoot(source, filepath.Join(t.TempDir(), "worker"))
	if err != nil {
		t.Fatal(err)
	}
	journal := filepath.Join(root, "headless-profile", "RimGovernorClockEvents")
	row := filepath.Join(journal, "00000000000000000001.xml")
	if err := os.MkdirAll(journal, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(row, []byte("<row/>"), 0644); err != nil {
		t.Fatal(err)
	}
	configuration, err := Prepare(root)
	if err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	if _, err := os.Stat(row); err != nil {
		t.Fatalf("Prepare touched the live clock journal: %v", err)
	}
	if got, err := ClockJournalDir(configuration); err != nil || got != journal {
		t.Fatalf("ClockJournalDir = %q, %v; want %q", got, err, journal)
	}
	if err := ClearStaleClockJournal(configuration); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(journal); !os.IsNotExist(err) {
		t.Fatalf("stale clock journal survived ClearStaleClockJournal: %v", err)
	}
	// Clearing an already-empty journal, or one of a config that names no
	// save-data folder, is not an error.
	if err := ClearStaleClockJournal(configuration); err != nil {
		t.Fatal(err)
	}
	if err := ClearStaleClockJournal(filepath.Join(root, "missing")); err != nil {
		t.Fatal(err)
	}
}

func TestPrepareMirrorsEveryVariantSave(t *testing.T) {
	source := writeSourceRoot(t)
	destination := filepath.Join(t.TempDir(), "worker")
	root, err := IsolatedRoot(source, destination)
	if err != nil {
		t.Fatal(err)
	}
	// A variant save (e.g. deposited by variantgen.Generate) lives directly
	// in the worker root's profile/Saves, same as the hand-staged baseline;
	// IsolatedRoot itself is a separate, currently-unused-in-production
	// bootstrap step and not part of this path.
	if err := os.WriteFile(filepath.Join(root, "profile", "Saves", "RimGovernor-tribal8-scarce-wood.rws"), []byte("variant-save-data"), 0644); err != nil {
		t.Fatal(err)
	}
	if _, err := Prepare(root); err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "headless-profile", "Saves", "RimGovernor-tribal8-baseline.rws")); err != nil {
		t.Fatalf("headless-profile baseline save missing: %v", err)
	}
	got, err := os.ReadFile(filepath.Join(root, "headless-profile", "Saves", "RimGovernor-tribal8-scarce-wood.rws"))
	if err != nil {
		t.Fatalf("headless-profile variant save missing: %v", err)
	}
	if string(got) != "variant-save-data" {
		t.Fatalf("variant save contents = %q, want %q", got, "variant-save-data")
	}
}

func TestPrepareFailsWithoutRequiredBaseline(t *testing.T) {
	source := writeSourceRoot(t)
	destination := filepath.Join(t.TempDir(), "worker")
	root, err := IsolatedRoot(source, destination)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(root, "profile", "Saves", "RimGovernor-tribal8-baseline.rws")); err != nil {
		t.Fatal(err)
	}
	if _, err := Prepare(root); err == nil {
		t.Fatal("expected Prepare to fail without the required baseline save")
	}
}

func TestPrepareRenderedRewritesWindowedArgs(t *testing.T) {
	source := writeSourceRoot(t)
	destination := filepath.Join(t.TempDir(), "worker")
	root, err := IsolatedRoot(source, destination)
	if err != nil {
		t.Fatal(err)
	}
	configuration, err := PrepareRendered(root)
	if err != nil {
		t.Fatalf("PrepareRendered failed: %v", err)
	}
	config, err := loadConfig(filepath.Join(configuration, "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	game, err := gameSection(config)
	if err != nil {
		t.Fatal(err)
	}
	args, _ := game["args"].([]any)
	joined := map[string]bool{}
	for _, a := range args {
		joined[a.(string)] = true
	}
	for _, flag := range []string{"-screen-fullscreen", "-screen-width", "-screen-height", "-rimgovernor-pause-on-load", "-rimgovernor-test-acceleration"} {
		if !joined[flag] {
			t.Fatalf("windowed args missing %s: %v", flag, args)
		}
	}
	if joined["-batchmode"] || joined["-nographics"] {
		t.Fatalf("rendered args must not include batch flags: %v", args)
	}
	// The saved prefs override the launch arguments at startup, so the
	// rendered profile's Prefs.xml must itself say windowed.
	prefs, err := os.ReadFile(filepath.Join(root, "profile", "Config", "Prefs.xml"))
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"<fullscreen>False</fullscreen>", "<screenWidth>1280</screenWidth>", "<screenHeight>720</screenHeight>"} {
		if !strings.Contains(string(prefs), want) {
			t.Fatalf("rendered Prefs.xml missing %s:\n%s", want, prefs)
		}
	}
}

func TestGABSExecutableDefaultsToRelativePath(t *testing.T) {
	root := t.TempDir()
	configDir := filepath.Join(root, "config")
	if err := os.MkdirAll(configDir, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(configDir, "config.json"), []byte(`{"version":"1.0"}`), 0644); err != nil {
		t.Fatal(err)
	}
	got, err := GABSExecutable(root, configDir)
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join(root, "gabs", "gabs-v1.1.1-windows-amd64", "gabs.exe")
	if got != want {
		t.Fatalf("GABSExecutable = %q, want %q", got, want)
	}
}

// li returns the text of every direct <li> child element, in document order.
func (e *xmlElem) li() []string {
	var out []string
	if e == nil {
		return out
	}
	for _, kid := range e.kids {
		if kid.elem != nil && kid.elem.name.Local == "li" {
			out = append(out, kid.elem.text())
		}
	}
	return out
}

func TestSaveExpansions(t *testing.T) {
	root := t.TempDir()
	saves := filepath.Join(root, "profile", "Saves")
	if err := os.MkdirAll(saves, 0755); err != nil {
		t.Fatal(err)
	}
	dlc := "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame>\n\t<meta>\n\t\t<gameVersion>1.6.4871 rev591</gameVersion>\n\t\t<modIds>\n\t\t\t<li>ludeon.rimworld</li>\n\t\t\t<li>ludeon.rimworld.royalty</li>\n\t\t\t<li>Ludeon.RimWorld.Biotech</li>\n\t\t\t<li>brrainz.harmony</li>\n\t\t</modIds>\n\t</meta>\n</savegame>"
	core := "<savegame><meta><modIds><li>ludeon.rimworld</li><li>brrainz.harmony</li></modIds></meta></savegame>"
	for name, body := range map[string]string{"dlc": dlc, "core": core, "broken": "<savegame/>"} {
		if err := os.WriteFile(filepath.Join(saves, name+".rws"), []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
	}
	got, err := SaveExpansions(root, "dlc")
	if err != nil || strings.Join(got, ",") != "ludeon.rimworld.royalty,ludeon.rimworld.biotech" {
		t.Fatalf("SaveExpansions(dlc) = %v, %v", got, err)
	}
	if got, err := SaveExpansions(root, "core"); err != nil || len(got) != 0 {
		t.Fatalf("SaveExpansions(core) = %v, %v", got, err)
	}
	if _, err := SaveExpansions(root, "broken"); err == nil {
		t.Fatal("expected error for a save without modIds")
	}
	if _, err := SaveExpansions(root, "missing"); err == nil {
		t.Fatal("expected error for a missing save")
	}

	cfg := &Config{Root: root}
	if err := cfg.UseSaveExpansions("", ""); err != nil || cfg.Expansions != nil {
		t.Fatalf("all-empty names must leave Expansions nil: %v, %v", cfg.Expansions, err)
	}
	if err := cfg.UseSaveExpansions("core"); err != nil || cfg.Expansions == nil || len(cfg.Expansions) != 0 {
		t.Fatalf("core save must pin Core-only explicitly: %#v, %v", cfg.Expansions, err)
	}
	if err := cfg.UseSaveExpansions("dlc", "", "dlc"); err != nil || strings.Join(cfg.Expansions, ",") != "ludeon.rimworld.royalty,ludeon.rimworld.biotech" {
		t.Fatalf("union: %v, %v", cfg.Expansions, err)
	}
}

// A kept process runs with the ModsConfig.xml it was launched with, so
// OpenGame compares the snapshot a fresh launch records against what the
// current Prepare wrote and relaunches on a difference (#166).
func TestLaunchedMismatchOnExpansions(t *testing.T) {
	source := writeSourceRoot(t)
	root, err := IsolatedRoot(source, filepath.Join(t.TempDir(), "worker"))
	if err != nil {
		t.Fatal(err)
	}
	configuration, err := Prepare(root)
	if err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	snapshot := filepath.Join(root, "headless-profile", LaunchedModsFile)
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "unrecorded" {
		t.Fatalf("before any launch: reason %q, err %v; want unrecorded", reason, err)
	}
	if err := prepareFreshLaunch(configuration); err != nil {
		t.Fatal(err)
	}
	launched, err := ActiveMods(snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{CorePackage, "brrainz.harmony", "brrainz.rimbridgeserver", casefold(NativePackage)}; strings.Join(launched, ",") != strings.Join(want, ",") {
		t.Fatalf("Core-only launch recorded %v, want %v", launched, want)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "" {
		t.Fatalf("same profile: reason %q, err %v; want reuse", reason, err)
	}
	// The next harness wants a DLC save's expansions: the Core-only process
	// cannot load it.
	if _, err := Prepare(root, "royalty", "biotech"); err != nil {
		t.Fatalf("Prepare with expansions failed: %v", err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "expansions" {
		t.Fatalf("DLC profile on a Core-only process: reason %q, err %v; want expansions", reason, err)
	}
	if err := prepareFreshLaunch(configuration); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "" {
		t.Fatalf("relaunched with the DLC profile: reason %q, err %v; want reuse", reason, err)
	}
	// And the reverse: a Core-only harness after a DLC process relaunches
	// too, rather than running slower with the extra defs.
	if _, err := Prepare(root); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "expansions" {
		t.Fatalf("Core-only profile on a DLC process: reason %q, err %v; want expansions", reason, err)
	}
	// A config naming no save-data folder records nothing and is not an error.
	if err := prepareFreshLaunch(filepath.Join(root, "missing")); err != nil {
		t.Fatal(err)
	}
}

// A kept process serves the DLLs it loaded, so a rebuilt package installed
// under it (new fixtures, say) must relaunch rather than fail discovery
// against the old catalog (#209).
func TestLaunchedMismatchOnPackage(t *testing.T) {
	source := writeSourceRoot(t)
	root, err := IsolatedRoot(source, filepath.Join(t.TempDir(), "worker"))
	if err != nil {
		t.Fatal(err)
	}
	configuration, err := Prepare(root)
	if err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	// A mod snapshot without a package snapshot is an older binary's launch.
	if err := RecordLaunchedMods(configuration); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "unrecorded" {
		t.Fatalf("mods recorded, package not: reason %q, err %v; want unrecorded", reason, err)
	}
	if err := prepareFreshLaunch(configuration); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "" {
		t.Fatalf("same package: reason %q, err %v; want reuse", reason, err)
	}
	dll := filepath.Join(source, "install", "Mods", "RimGovernor", "BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll")
	if err := os.WriteFile(dll, []byte("rebuilt with more fixtures"), 0644); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "package" {
		t.Fatalf("rebuilt package under a kept process: reason %q, err %v; want package", reason, err)
	}
	if err := prepareFreshLaunch(configuration); err != nil {
		t.Fatal(err)
	}
	if reason, err := LaunchedMismatch(configuration); err != nil || reason != "" {
		t.Fatalf("relaunched on the rebuilt package: reason %q, err %v; want reuse", reason, err)
	}
}

// The committed baseline is staged into a root that lacks it and replaces
// an older copy (the DLC one every root carried before #192); a root that
// already holds the committed bytes is left alone (#192).
func TestPrepareStagesCommittedBaseline(t *testing.T) {
	source := writeSourceRoot(t)
	root, err := IsolatedRoot(source, filepath.Join(t.TempDir(), "worker"))
	if err != nil {
		t.Fatal(err)
	}
	committed := filepath.Join(t.TempDir(), "saves")
	if err := os.MkdirAll(committed, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(committed, BaselineSave), []byte("committed core-only save"), 0644); err != nil {
		t.Fatal(err)
	}
	withCommittedSaves(t, committed)
	target := filepath.Join(root, "profile", "Saves", BaselineSave)
	// IsolatedRoot copied the source root's stale "save-data" baseline.
	if _, err := Prepare(root); err != nil {
		t.Fatalf("Prepare failed: %v", err)
	}
	if got, _ := os.ReadFile(target); string(got) != "committed core-only save" {
		t.Fatalf("older baseline not replaced: %q", got)
	}
	if got, _ := os.ReadFile(filepath.Join(root, "headless-profile", "Saves", BaselineSave)); string(got) != "committed core-only save" {
		t.Fatalf("headless profile got %q", got)
	}
	if err := os.Remove(target); err != nil {
		t.Fatal(err)
	}
	if _, err := PrepareRendered(root); err != nil {
		t.Fatalf("PrepareRendered failed: %v", err)
	}
	if got, _ := os.ReadFile(target); string(got) != "committed core-only save" {
		t.Fatalf("missing baseline not staged: %q", got)
	}
	before, err := os.Stat(target)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := Prepare(root); err != nil {
		t.Fatal(err)
	}
	if after, _ := os.Stat(target); !after.ModTime().Equal(before.ModTime()) {
		t.Fatal("an up-to-date baseline was rewritten")
	}
}

// The smoke run of #388 launched a worker whose profile path was 224
// characters up to the journal directory: the 24-character row name fit
// MAX_PATH, the 40-character temporary did not, and every publication
// failed. Prepare now refuses a profile that cannot hold a row at all.
func TestRequireProfilePathFitsBoundsWindowsMaxPath(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("only Windows bounds paths at MAX_PATH")
	}
	if MaxProfilePathLength != 211 {
		t.Fatalf("MaxProfilePathLength = %d, want 211 (259 - 48)", MaxProfilePathLength)
	}
	fits := `C:\` + strings.Repeat("a", MaxProfilePathLength-3)
	if err := RequireProfilePathFits(fits); err != nil {
		t.Fatalf("%d-character profile refused: %v", len(fits), err)
	}
	if err := RequireProfilePathFits(fits + "b"); err == nil || !strings.Contains(err.Error(), "MAX_PATH") {
		t.Fatalf("%d-character profile accepted: %v", len(fits)+1, err)
	}
}
