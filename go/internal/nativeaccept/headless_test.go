package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
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
	if err := PrepareNativeModConfig(path); err != nil {
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

func TestPrepareNativeModConfigMissingActiveMods(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "ModsConfig.xml")
	if err := os.WriteFile(path, []byte("<ModsConfigData><version>1</version></ModsConfigData>"), 0644); err != nil {
		t.Fatal(err)
	}
	if err := PrepareNativeModConfig(path); err == nil {
		t.Fatal("expected error for missing activeMods")
	}
}

func writeSourceRoot(t *testing.T) string {
	t.Helper()
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
	if err := os.WriteFile(filepath.Join(profile, "Config", "Prefs.xml"), []byte("<prefs/>"), 0644); err != nil {
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
	foundBatch, foundNoGraphics := false, false
	for _, a := range joined {
		if a == "-batchmode" {
			foundBatch = true
		}
		if a == "-nographics" {
			foundNoGraphics = true
		}
	}
	if !foundBatch || !foundNoGraphics {
		t.Fatalf("headless args missing batch/nographics flags: %v", joined)
	}
	if _, err := os.Stat(filepath.Join(root, "headless-profile", "Config", "ModsConfig.xml")); err != nil {
		t.Fatalf("headless-profile ModsConfig.xml missing: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "headless-profile", "Saves", "RimGovernor-tribal8-baseline.rws")); err != nil {
		t.Fatalf("headless-profile baseline save missing: %v", err)
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
	for _, flag := range []string{"-screen-fullscreen", "-screen-width", "-screen-height", "-rimgovernor-pause-on-load"} {
		if !joined[flag] {
			t.Fatalf("windowed args missing %s: %v", flag, args)
		}
	}
	if joined["-batchmode"] || joined["-nographics"] {
		t.Fatalf("rendered args must not include batch flags: %v", args)
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
