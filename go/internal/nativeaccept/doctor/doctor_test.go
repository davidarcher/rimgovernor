package doctor

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

func byName(checks []Check, name string) Check {
	for _, c := range checks {
		if c.Name == name {
			return c
		}
	}
	return Check{Name: name, Status: -1}
}

func write(t *testing.T, path, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}
}

const coreSave = "<savegame><meta><modIds><li>ludeon.rimworld</li><li>brrainz.harmony</li></modIds></meta></savegame>"

// fakeRoot is a checkout-less root whose config launches a game copy with
// a complete (unhashed) native package and a Core-only baseline.
func fakeRoot(t *testing.T) (Options, setup.Layout) {
	t.Helper()
	repo := t.TempDir()
	l := setup.NewLayout(repo)
	write(t, filepath.Join(l.Root, "config", "config.json"), setup.RenderConfig(l))
	write(t, filepath.Join(l.GameCopy, "RimWorldWin64.exe"), "")
	write(t, filepath.Join(l.Root, filepath.FromSlash(setup.GABSRelative)), "")
	pkg := filepath.Join(l.GameCopy, "Mods", "RimGovernor")
	write(t, filepath.Join(pkg, "About", "About.xml"), "<ModMetaData><packageId>"+na.NativePackage+"</packageId></ModMetaData>")
	write(t, filepath.Join(pkg, "Assemblies", "RimGovernor.Runtime.dll"), "")
	write(t, filepath.Join(pkg, "BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"), "")
	write(t, filepath.Join(l.Root, "profile", "Config", "ModsConfig.xml"), setup.RenderModsConfig("1.6"))
	write(t, filepath.Join(l.Root, "profile", "Saves", na.BaselineSave), coreSave)
	// Repo is set so the checks never walk up from the temp dir into a
	// real checkout; it holds no committed save.
	return Options{Root: l.Root, Repo: repo}, l
}

func TestMissingRootFails(t *testing.T) {
	root := filepath.Join(t.TempDir(), "bridge")
	checks := Run(context.Background(), Options{Root: root, Repo: t.TempDir()})
	c := byName(checks, "root")
	if c.Status != Fail || !strings.Contains(c.Fix, "acceptance setup") {
		t.Fatalf("root = %+v", c)
	}
	for _, name := range []string{"game-copy", "gabs", "mod", "game"} {
		if got := byName(checks, name); got.Status != -1 {
			t.Errorf("%s ran without a config: %+v", name, got)
		}
	}
	if !Failed(checks) {
		t.Fatal("Failed = false")
	}
}

func TestHealthyRootPasses(t *testing.T) {
	o, _ := fakeRoot(t)
	checks := Run(context.Background(), o)
	for _, name := range []string{"root", "game-copy", "gabs", "baseline", "mods-config", "output"} {
		if c := byName(checks, name); c.Status != OK {
			t.Errorf("%s = %+v", name, c)
		}
	}
	// No manifest: the stale check cannot run, which is a warning, not
	// a refusal.
	if c := byName(checks, "mod"); c.Status != Warn || !strings.Contains(c.Detail, na.PackageManifestName) {
		t.Errorf("mod = %+v", c)
	}
	if Failed(checks) {
		var b bytes.Buffer
		Write(&b, checks, true)
		t.Fatalf("Failed on a healthy root:\n%s", b.String())
	}
}

func TestGameCopyPathBound(t *testing.T) {
	long := strings.Repeat("x", setup.MaxGameCopyPath+1)
	if c := gameCopyPath(long); c.Status != Fail || !strings.Contains(c.Fix, "worktree") {
		t.Fatalf("over the bound: %+v", c)
	}
	if c := gameCopyPath(`C:\short`); c.Status != OK {
		t.Fatalf("short: %+v", c)
	}
}

func TestBaselineDLCFails(t *testing.T) {
	o, l := fakeRoot(t)
	write(t, filepath.Join(l.Root, "profile", "Saves", na.BaselineSave), "<savegame><meta><modIds><li>ludeon.rimworld</li><li>Ludeon.RimWorld.Royalty</li></modIds></meta></savegame>")
	c := byName(Run(context.Background(), o), "baseline")
	if c.Status != Fail || !strings.Contains(c.Detail, "ludeon.rimworld.royalty") {
		t.Fatalf("baseline = %+v", c)
	}
}

func TestBaselineMissingEverywhereFails(t *testing.T) {
	o, l := fakeRoot(t)
	os.Remove(filepath.Join(l.Root, "profile", "Saves", na.BaselineSave))
	c := byName(Run(context.Background(), o), "baseline")
	if c.Status != Fail || !strings.Contains(c.Fix, na.CommittedSavesDir) {
		t.Fatalf("baseline = %+v", c)
	}
}

func TestBaselineStagedFromCheckout(t *testing.T) {
	o, l := fakeRoot(t)
	os.Remove(filepath.Join(l.Root, "profile", "Saves", na.BaselineSave))
	write(t, filepath.Join(o.Repo, filepath.FromSlash(na.CommittedSavesDir), na.BaselineSave), coreSave)
	c := byName(Run(context.Background(), o), "baseline")
	if c.Status != OK || !strings.Contains(c.Detail, "Prepare copies") {
		t.Fatalf("baseline = %+v", c)
	}
}

func TestModsConfigWithoutCoreFails(t *testing.T) {
	o, l := fakeRoot(t)
	write(t, filepath.Join(l.Root, "profile", "Config", "ModsConfig.xml"), "<ModsConfigData><activeMods><li>brrainz.harmony</li></activeMods></ModsConfigData>")
	c := byName(Run(context.Background(), o), "mods-config")
	if c.Status != Fail || !strings.Contains(c.Detail, na.CorePackage) {
		t.Fatalf("mods-config = %+v", c)
	}
}

func TestOccupiedCaseOutputFails(t *testing.T) {
	o, l := fakeRoot(t)
	o.Cases = []string{"smoke/identity", "bed/assign"}
	write(t, filepath.Join(l.Root, "acceptance", "smoke", "identity", "result.json"), "{}")
	c := byName(Run(context.Background(), o), "output")
	if c.Status != Fail || !strings.Contains(c.Detail, "smoke/identity") || strings.Contains(c.Detail, "bed/assign") {
		t.Fatalf("output = %+v", c)
	}
	// Without case names, earlier results are informational.
	o.Cases = nil
	if c := byName(Run(context.Background(), o), "output"); c.Status != OK || !strings.Contains(c.Detail, "1 earlier") {
		t.Fatalf("output without cases = %+v", c)
	}
}

func TestJournalBacklogOnlyUnderRunningGame(t *testing.T) {
	o, l := fakeRoot(t)
	headless := filepath.Join(l.Root, "config-headless")
	profile := filepath.Join(l.Root, "headless-profile")
	config, _ := json.Marshal(map[string]any{"games": map[string]any{setup.GameID: map[string]any{
		"launchMode": "DirectPath", "target": filepath.Join(l.GameCopy, "RimWorldWin64.exe"), "workingDir": l.GameCopy,
		"args": []string{"-savedatafolder=" + profile, "-batchmode"},
	}}})
	write(t, filepath.Join(headless, "config.json"), string(config))
	dir, err := na.ClockJournalDir(headless)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i <= ClockJournalBacklog; i++ {
		write(t, filepath.Join(dir, fmt.Sprintf("%020d.xml", i+1)), "<row/>")
	}
	if c := journal(o, false); c.Status != OK || !strings.Contains(c.Detail, "cleared") {
		t.Fatalf("no game: %+v", c)
	}
	if c := journal(o, true); c.Status != Warn || !strings.Contains(c.Fix, "acceptance stop") {
		t.Fatalf("kept game: %+v", c)
	}
}

func TestWriteOnlyFailing(t *testing.T) {
	checks := []Check{{Name: "a", Status: OK, Detail: "fine"}, {Name: "b", Status: Warn, Detail: "meh", Fix: "do x"}, {Name: "c", Status: Fail, Detail: "bad", Fix: "do y"}}
	var b bytes.Buffer
	Write(&b, checks, true)
	out := b.String()
	if strings.Contains(out, "fine") || !strings.Contains(out, "warn  b") || !strings.Contains(out, "FAIL  c") || !strings.Contains(out, "fix: do y") {
		t.Fatalf("onlyFailing output:\n%s", out)
	}
	b.Reset()
	Write(&b, checks, false)
	if !strings.Contains(b.String(), "ok    a") {
		t.Fatalf("full output:\n%s", b.String())
	}
}

func TestSaveModIDs(t *testing.T) {
	path := filepath.Join(t.TempDir(), "x.rws")
	write(t, path, "<savegame>\n\t<meta>\n\t\t<modIds>\n\t\t\t<li>Ludeon.RimWorld</li>\n\t\t\t<li>brrainz.harmony</li>\n\t\t</modIds>\n\t</meta>\n</savegame>")
	ids, err := saveModIDs(path)
	if err != nil || strings.Join(ids, ",") != "ludeon.rimworld,brrainz.harmony" {
		t.Fatalf("ids = %v, %v", ids, err)
	}
	write(t, path, "<savegame/>")
	if _, err := saveModIDs(path); err == nil {
		t.Fatal("no modIds accepted")
	}
}

// fixtureRepo is a checkout whose scripts/fixtures register one op each
// under PowerFixture and FarmFixture.
func fixtureRepo(t *testing.T) string {
	t.Helper()
	repo := t.TempDir()
	write(t, filepath.Join(repo, "scripts", "fixtures", "PowerFixture.cs"), `[Tool("test/power_prepare")] class PowerFixture {}`)
	write(t, filepath.Join(repo, "scripts", "fixtures", "FarmFixture.cs"), `[Tool("test/farm_prepare")] class FarmFixture {}`)
	return repo
}

func TestMissingFixturesNamesTheClassesTheBuildLacks(t *testing.T) {
	repo := fixtureRepo(t)
	got := MissingFixtures(repo, []string{"PowerFixture", "ShutdownFixture"}, []string{"test/power_prepare", "test/farm_prepare", "test/nobody"})
	if strings.Join(got, ",") != "FarmFixture" {
		t.Fatalf("MissingFixtures = %v", got)
	}
	if got := MissingFixtures(repo, []string{"PowerFixture"}, []string{"test/power_prepare"}); got != nil {
		t.Fatalf("installed fixture reported missing: %v", got)
	}
	if got := MissingFixtures("", nil, []string{"test/power_prepare"}); got != nil {
		t.Fatalf("no repo: %v", got)
	}
}

func TestStaleModFailsWithHealCode(t *testing.T) {
	o, l := fakeRoot(t)
	pkg := filepath.Join(l.GameCopy, "Mods", "RimGovernor")
	write(t, filepath.Join(pkg, na.PackageManifestName), `{"role":"fixture","fixtures":["PowerFixture"],"sourceRevision":"abcdef0123456789","sourceTree":"not-this-worktree"}`)
	// RequireCurrentPackage compares against the checkout enclosing the
	// working directory: this test's, whose tree never hashes to the
	// manifest's.
	checks := Run(context.Background(), o)
	c := byName(checks, "mod")
	if c.Status != Fail || c.Code != HealStaleMod {
		t.Fatalf("mod = %+v", c)
	}
	if !strings.Contains(c.Fix, "acceptance setup") {
		t.Fatalf("fix = %q", c.Fix)
	}
}

func TestSetupHintSkipsEmptyFlags(t *testing.T) {
	if got := setupHint("", "", "-rebuild"); got != "go run ./internal/nativeaccept/cmd/acceptance setup -rebuild (from go/)" {
		t.Fatalf("setupHint = %q", got)
	}
}
