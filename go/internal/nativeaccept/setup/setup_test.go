package setup

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestLibraryPaths(t *testing.T) {
	vdf := `"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Program Files (x86)\\Steam"
		"label"		""
	}
	"1"
	{
		"path"		"D:\\SteamLibrary"
	}
}`
	got := LibraryPaths(vdf)
	want := []string{`C:\Program Files (x86)\Steam`, `D:\SteamLibrary`}
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Fatalf("LibraryPaths = %v, want %v", got, want)
	}
}

func TestParseValidateSet(t *testing.T) {
	src := `param(
    [Parameter(Mandatory = $true)][string]$X,
    [ValidateSet('BFixture', 'AFixture',
        'CFixture')]
    [string[]]$Fixture = @()
)`
	got, err := ParseValidateSet(src)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(got, ",") != "AFixture,BFixture,CFixture" {
		t.Fatalf("ParseValidateSet = %v", got)
	}
	if _, err := ParseValidateSet("param([string]$X)"); err == nil {
		t.Fatal("no ValidateSet accepted")
	}
}

func TestAllFixturesReadsTheBuildScript(t *testing.T) {
	repo, ok := findRepo(t)
	if !ok {
		t.Skip("no checkout")
	}
	all, err := AllFixtures(repo)
	if err != nil {
		t.Fatal(err)
	}
	has := map[string]bool{}
	for _, f := range all {
		has[f] = true
	}
	for _, f := range alwaysFixtures {
		if !has[f] {
			t.Errorf("ValidateSet lacks %s, which every fixture build carries", f)
		}
	}
	for _, f := range all {
		if !strings.HasSuffix(f, "Fixture") {
			t.Errorf("odd fixture class %q", f)
		}
		if _, err := os.Stat(filepath.Join(repo, "scripts", "fixtures", f+".cs")); err != nil {
			t.Errorf("%s has no source under scripts/fixtures", f)
		}
	}
}

func TestExpectedFixtures(t *testing.T) {
	if got := expectedFixtures(nil); got != nil {
		t.Fatalf("production build expects %v", got)
	}
	got := expectedFixtures([]string{"UpkeepFixture", "ShutdownFixture"})
	want := "DebugStartFixture,FreezeNeedsFixture,LetterFixture,QuietStorytellerFixture,ShutdownFixture,UpkeepFixture"
	if strings.Join(got, ",") != want {
		t.Fatalf("expectedFixtures = %v", got)
	}
}

func TestRenderConfigLaunchesTheCopy(t *testing.T) {
	l := NewLayout(filepath.Join(t.TempDir(), "wt"))
	var config struct {
		Games map[string]struct {
			LaunchMode string   `json:"launchMode"`
			Target     string   `json:"target"`
			WorkingDir string   `json:"workingDir"`
			Args       []string `json:"args"`
		} `json:"games"`
	}
	if err := json.Unmarshal([]byte(RenderConfig(l)), &config); err != nil {
		t.Fatal(err)
	}
	g, ok := config.Games[GameID]
	if !ok {
		t.Fatalf("no %s game: %v", GameID, config.Games)
	}
	if g.LaunchMode != "DirectPath" || g.WorkingDir != l.GameCopy || g.Target != filepath.Join(l.GameCopy, "RimWorldWin64.exe") {
		t.Fatalf("game = %+v", g)
	}
	if g.Args[0] != "-savedatafolder="+filepath.Join(l.Root, "profile") {
		t.Fatalf("args = %v", g.Args)
	}
}

func TestRenderModsConfigActivatesTheBridgeStack(t *testing.T) {
	out := RenderModsConfig("1.6.4871 rev590")
	for _, want := range []string{"<version>1.6.4871 rev590</version>", "<li>ludeon.rimworld</li>", "<li>brrainz.harmony</li>", "<li>brrainz.rimbridgeserver</li>", "<li>davidarcher.rimgovernor.native</li>"} {
		if !strings.Contains(out, want) {
			t.Errorf("ModsConfig lacks %s:\n%s", want, out)
		}
	}
}

func TestPeerCheckoutsListsMainAndSiblings(t *testing.T) {
	main := t.TempDir()
	if err := os.MkdirAll(filepath.Join(main, ".git", "worktrees", "a"), 0755); err != nil {
		t.Fatal(err)
	}
	for _, wt := range []string{"a", "b"} {
		dir := filepath.Join(main, ".claude", "worktrees", wt)
		if err := os.MkdirAll(dir, 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, ".git"), []byte("gitdir: "+filepath.Join(main, ".git", "worktrees", wt)+"\n"), 0644); err != nil {
			t.Fatal(err)
		}
	}
	a := filepath.Join(main, ".claude", "worktrees", "a")
	got := PeerCheckouts(a)
	if len(got) != 3 || got[0] != absClean(a) || got[1] != absClean(main) {
		t.Fatalf("PeerCheckouts = %v", got)
	}
	if got := PeerCheckouts(main); len(got) != 3 || got[0] != absClean(main) {
		t.Fatalf("PeerCheckouts(main) = %v", got)
	}
}

func TestEnsureJunction(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("junctions are NTFS")
	}
	dir := t.TempDir()
	target, other := filepath.Join(dir, "target"), filepath.Join(dir, "other")
	for _, d := range []string{target, other} {
		if err := os.Mkdir(d, 0755); err != nil {
			t.Fatal(err)
		}
	}
	link := filepath.Join(dir, "link")
	if changed, err := ensureJunction(link, target); err != nil || !changed {
		t.Fatalf("create: %v %v", changed, err)
	}
	if changed, err := ensureJunction(link, target); err != nil || changed {
		t.Fatalf("repeat: %v %v", changed, err)
	}
	if changed, err := ensureJunction(link, other); err != nil || !changed {
		t.Fatalf("repoint: %v %v", changed, err)
	}
	if got, _ := os.Readlink(link); !strings.EqualFold(strings.TrimPrefix(got, `\\?\`), other) {
		t.Fatalf("link -> %s", got)
	}
	if _, err := ensureJunction(target, other); err == nil {
		t.Fatal("a real directory was replaced")
	}
}

func TestCopyIfChangedSkipsCurrentFiles(t *testing.T) {
	dir := t.TempDir()
	from, to := filepath.Join(dir, "a"), filepath.Join(dir, "b", "a")
	if err := os.WriteFile(from, []byte("x"), 0644); err != nil {
		t.Fatal(err)
	}
	if n, err := copyIfChanged(from, to); err != nil || n != 1 {
		t.Fatalf("first copy: %d %v", n, err)
	}
	if n, err := copyIfChanged(from, to); err != nil || n != 0 {
		t.Fatalf("repeat: %d %v", n, err)
	}
	if err := os.WriteFile(from, []byte("xy"), 0644); err != nil {
		t.Fatal(err)
	}
	if n, err := copyIfChanged(from, to); err != nil || n != 1 {
		t.Fatalf("changed: %d %v", n, err)
	}
}

func findRepo(t *testing.T) (string, bool) {
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "scripts", "build_native_mod.ps1")); err == nil {
			return dir, true
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", false
		}
		dir = parent
	}
}

func TestFindPeerGABSPrefersOwnBridgeAndGlobsReleases(t *testing.T) {
	main := t.TempDir()
	if err := os.MkdirAll(filepath.Join(main, ".git", "worktrees", "a"), 0755); err != nil {
		t.Fatal(err)
	}
	wt := filepath.Join(main, ".claude", "worktrees", "a")
	if err := os.MkdirAll(wt, 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(wt, ".git"), []byte("gitdir: "+filepath.Join(main, ".git", "worktrees", "a")+"\n"), 0644); err != nil {
		t.Fatal(err)
	}
	install := func(root, release string) string {
		exe := filepath.Join(root, ".rimgovernor", "bridge", "gabs", release, "gabs.exe")
		if err := os.MkdirAll(filepath.Dir(exe), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(exe, []byte("MZ"), 0644); err != nil {
			t.Fatal(err)
		}
		return absClean(exe)
	}
	if got, _, searched := findPeerGABS(wt); got != "" || len(searched) != 2 {
		t.Fatalf("empty layout: found %q, searched %v", got, searched)
	}
	// The main checkout's is found through the worktree.
	mainExe := install(main, "gabs-v1.1.1-windows-amd64")
	if got, source, _ := findPeerGABS(wt); got != mainExe || source != "peer checkout "+absClean(main) {
		t.Fatalf("main's gabs.exe: got %q (%s)", got, source)
	}
	// The worktree's own comes first, under whatever release directory it
	// was installed with (#352).
	ownExe := install(wt, "gabs-v1.2.0-windows-amd64")
	if got, source, _ := findPeerGABS(wt); got != ownExe || source != "peer checkout "+absClean(wt) {
		t.Fatalf("own gabs.exe: got %q (%s)", got, source)
	}
	// The conventional path wins over other releases in the same root.
	ownConventional := install(wt, "gabs-v1.1.1-windows-amd64")
	if got, _, _ := findPeerGABS(wt); got != ownConventional {
		t.Fatalf("conventional gabs.exe: got %q", got)
	}
}
