package setup

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Layout is where a worktree's private environment lives, all under
// <repo>/.rimgovernor (git-ignored).
type Layout struct {
	Repo string
	// GameCopy is the private RimWorld install (native-rimworld).
	GameCopy string
	// Root is the bridge root `acceptance run -root` takes (bridge).
	Root string
	// Builds holds mod build outputs (native-builds/<role>-<stamp>).
	Builds string
	// Bin holds acceptance.exe and rimgovernor.exe.
	Bin string
}

// NewLayout is the conventional layout for repo.
func NewLayout(repo string) Layout {
	repo = absClean(repo)
	private := filepath.Join(repo, ".rimgovernor")
	return Layout{
		Repo:     repo,
		GameCopy: filepath.Join(private, "native-rimworld"),
		Root:     filepath.Join(private, "bridge"),
		Builds:   filepath.Join(private, "native-builds"),
		Bin:      filepath.Join(private, "bin"),
	}
}

// Options drive Run.
type Options struct {
	Layout Layout
	Inputs *Inputs
	// Fixtures are the -Fixture classes the mod build takes; nil means
	// every class build_native_mod.ps1 accepts (AllFixtures), and
	// Production means none (a fixture-less build).
	Fixtures   []string
	Production bool
	// Rebuild forces a mod build even when the installed one is current.
	Rebuild bool
	// SkipMod leaves the installed mod alone (no build, no install).
	SkipMod bool
	// SkipBinaries leaves .rimgovernor/bin alone.
	SkipBinaries bool
	// Log receives progress lines.
	Log io.Writer
}

// Summary is what Run did, for the closing report.
type Summary struct {
	Layout      Layout
	Inputs      *Inputs
	Fixtures    []string
	ModBuilt    bool
	ModBuild    string
	ModReason   string
	Binaries    []string
	Changes     []string
	RunCommand  string
	StopCommand string
}

// Run makes the layout complete and current: the game copy, the bridge
// root, the installed mod and the binaries, each step idempotent and
// reporting what it changed.
func Run(ctx context.Context, o Options) (*Summary, error) {
	if o.Log == nil {
		o.Log = io.Discard
	}
	if runtime.GOOS != "windows" {
		return nil, errors.New("acceptance setup builds an NTFS-junctioned copy of a Windows RimWorld install; it only runs on Windows")
	}
	l := o.Layout
	s := &Summary{Layout: l, Inputs: o.Inputs}
	if n := len(l.GameCopy); n > MaxGameCopyPath {
		return nil, fmt.Errorf("game copy path is %d characters (%s); RimWorld memory-maps Data files by full path and fails past MAX_PATH (observed: every Defs XML \"Could not open file\", the bridge assembly not loading). Keep the worktree under a path of at most %d characters", n, l.GameCopy, MaxGameCopyPath-len(filepath.FromSlash("/.rimgovernor/native-rimworld")))
	}
	note := func(format string, args ...any) {
		line := fmt.Sprintf(format, args...)
		s.Changes = append(s.Changes, line)
		fmt.Fprintln(o.Log, line)
	}
	fmt.Fprintf(o.Log, "worktree  %s\nrimworld  %s (%s)\nharmony   %s (%s)\ngabs      %s (%s)\n",
		l.Repo, o.Inputs.RimWorldDir, o.Inputs.Sources["rimworld"], o.Inputs.Harmony, o.Inputs.Sources["harmony"], o.Inputs.GABS, o.Inputs.Sources["gabs"])

	if err := gameCopy(o.Inputs.RimWorldDir, l.GameCopy, note); err != nil {
		return s, fmt.Errorf("game copy: %w", err)
	}
	if err := bridgeRoot(l, o.Inputs, note); err != nil {
		return s, fmt.Errorf("bridge root: %w", err)
	}
	if !o.SkipMod {
		if err := installMod(ctx, o, s, note); err != nil {
			return s, fmt.Errorf("mod: %w", err)
		}
	}
	if !o.SkipBinaries {
		if err := buildBinaries(ctx, l, s, note); err != nil {
			return s, fmt.Errorf("binaries: %w", err)
		}
	}
	acceptance := filepath.Join(l.Bin, "acceptance.exe")
	s.RunCommand = fmt.Sprintf("%s run smoke/identity -root %s -rimgovernor %s", acceptance, l.Root, filepath.Join(l.Bin, "rimgovernor.exe"))
	s.StopCommand = fmt.Sprintf("%s stop -root %s", acceptance, l.Root)
	return s, nil
}

// MaxGameCopyPath bounds the copy's path: the deepest Core Defs path under
// it is ~120 characters and Mono's memory-mapped file open has no long-path
// support, so a copy past this fails to load the game's own XML. The
// .claude/worktrees layout sits at ~100.
const MaxGameCopyPath = 140

// JunctionDirs are the install's directories the copy junctions to Steam
// instead of copying: the bulk of the game, read-only for a DirectPath
// launch. Mods/RimBridgeServer is junctioned too (bridgeMod).
var JunctionDirs = []string{"Data", "RimWorldWin64_Data", "MonoBleedingEdge"}

const bridgeMod = "RimBridgeServer"

// gameCopy makes dest the private install: the loose top-level files
// copied (refreshed when Steam's differ in size or mtime), JunctionDirs
// and Mods/RimBridgeServer junctioned, every other top-level directory
// copied, and any other mod left out (ModsConfig decides activation; the
// copy holds only the bridge and our own package).
func gameCopy(steam, dest string, note func(string, ...any)) error {
	if err := os.MkdirAll(filepath.Join(dest, "Mods"), 0755); err != nil {
		return err
	}
	entries, err := os.ReadDir(steam)
	if err != nil {
		return err
	}
	junction := map[string]bool{}
	for _, d := range JunctionDirs {
		junction[d] = true
	}
	copied := 0
	for _, e := range entries {
		name := e.Name()
		from, to := filepath.Join(steam, name), filepath.Join(dest, name)
		switch {
		case junction[name]:
			changed, err := ensureJunction(to, from)
			if err != nil {
				return err
			}
			if changed {
				note("junction %s -> %s", to, from)
			}
		case name == "Mods":
			changed, err := ensureJunction(filepath.Join(to, bridgeMod), filepath.Join(from, bridgeMod))
			if err != nil {
				return err
			}
			if changed {
				note("junction %s -> %s", filepath.Join(to, bridgeMod), filepath.Join(from, bridgeMod))
			}
		case e.IsDir():
			n, err := copyTree(from, to)
			if err != nil {
				return err
			}
			copied += n
		default:
			n, err := copyIfChanged(from, to)
			if err != nil {
				return err
			}
			copied += n
		}
	}
	if copied > 0 {
		note("copied %d game files into %s", copied, dest)
	}
	return nil
}

// ensureJunction makes link a junction to target, replacing a junction
// that points elsewhere; a real directory in the way is an error (it is
// someone's data, not ours to delete).
func ensureJunction(link, target string) (bool, error) {
	if !isDir(target) {
		return false, fmt.Errorf("%s is not a directory", target)
	}
	// Go reports a junction as ModeIrregular (a reparse point that is
	// not a symlink) but Readlink still resolves it.
	info, err := os.Lstat(link)
	switch {
	case err == nil && info.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0:
		current, err := os.Readlink(link)
		if err == nil && strings.EqualFold(filepath.Clean(strings.TrimPrefix(current, `\\?\`)), filepath.Clean(target)) {
			return false, nil
		}
		if err := os.Remove(link); err != nil {
			return false, fmt.Errorf("replace junction %s: %w", link, err)
		}
	case err == nil:
		return false, fmt.Errorf("%s exists and is not a junction; move it aside (it should point at %s)", link, target)
	case !os.IsNotExist(err):
		return false, err
	}
	out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput()
	if err != nil {
		return false, fmt.Errorf("mklink /J %s %s: %v: %s", link, target, err, strings.TrimSpace(string(out)))
	}
	return true, nil
}

// copyIfChanged copies from over to unless to already has from's size and
// modification time; it returns how many files it wrote (0 or 1).
func copyIfChanged(from, to string) (int, error) {
	src, err := os.Stat(from)
	if err != nil {
		return 0, err
	}
	if dst, err := os.Stat(to); err == nil && dst.Size() == src.Size() && dst.ModTime().Equal(src.ModTime()) {
		return 0, nil
	}
	if err := os.MkdirAll(filepath.Dir(to), 0755); err != nil {
		return 0, err
	}
	if err := na.CopyFile(from, to); err != nil {
		return 0, err
	}
	return 1, os.Chtimes(to, src.ModTime(), src.ModTime())
}

// copyTree copies the regular files under from into to (copyIfChanged
// each), returning the count written.
func copyTree(from, to string) (int, error) {
	n := 0
	err := filepath.WalkDir(from, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		rel, err := filepath.Rel(from, path)
		if err != nil {
			return err
		}
		wrote, err := copyIfChanged(path, filepath.Join(to, rel))
		n += wrote
		return err
	})
	return n, err
}

// bridgeRoot makes root a GABS worker root for the copy: gabs.exe,
// config/config.json launching the copy's exe with root's profile,
// profile/Config with a Prefs.xml (the player's own when RimWorld has
// been run on this machine, else a minimal one; Prepare trims either) and
// a ModsConfig.xml for the core game plus the bridge and our package, and
// the committed baseline save under profile/Saves.
func bridgeRoot(l Layout, in *Inputs, note func(string, ...any)) error {
	gabs := filepath.Join(l.Root, filepath.FromSlash(GABSRelative))
	if n, err := copyIfChanged(in.GABS, gabs); err != nil {
		return err
	} else if n > 0 {
		note("installed %s", gabs)
	}
	configPath := filepath.Join(l.Root, "config", "config.json")
	want := RenderConfig(l)
	if current, err := os.ReadFile(configPath); err != nil || string(current) != want {
		if err := os.MkdirAll(filepath.Dir(configPath), 0755); err != nil {
			return err
		}
		if err := os.WriteFile(configPath, []byte(want), 0644); err != nil {
			return err
		}
		note("wrote %s", configPath)
	}
	config := filepath.Join(l.Root, "profile", "Config")
	if err := os.MkdirAll(config, 0755); err != nil {
		return err
	}
	prefs := filepath.Join(config, "Prefs.xml")
	if !isFile(prefs) {
		if player := playerPrefs(); player != "" {
			if err := na.CopyFile(player, prefs); err != nil {
				return err
			}
			note("copied the player's Prefs.xml from %s", player)
		} else {
			if err := os.WriteFile(prefs, []byte(minimalPrefs), 0644); err != nil {
				return err
			}
			note("wrote a minimal %s", prefs)
		}
	}
	mods := filepath.Join(config, "ModsConfig.xml")
	if !isFile(mods) {
		version, _ := os.ReadFile(filepath.Join(in.RimWorldDir, "Version.txt"))
		if err := os.WriteFile(mods, []byte(RenderModsConfig(strings.TrimSpace(string(version)))), 0644); err != nil {
			return err
		}
		note("wrote %s", mods)
	}
	save := filepath.Join(l.Root, "profile", "Saves", na.BaselineSave)
	if n, err := copyIfChanged(filepath.Join(l.Repo, filepath.FromSlash(na.CommittedSavesDir), na.BaselineSave), save); err != nil {
		return fmt.Errorf("baseline save: %w", err)
	} else if n > 0 {
		note("staged %s", save)
	}
	return nil
}

// GameID is the configured game every runner flag defaults to.
const GameID = "rimgovernor-trial"

// RenderConfig is the bridge root's config/config.json: a DirectPath
// launch of the copy's exe with the root's profile and log, windowed
// (Prepare rewrites the args for headless runs into config-headless).
func RenderConfig(l Layout) string {
	profile := filepath.Join(l.Root, "profile")
	config := map[string]any{
		"version": "1.0",
		"games": map[string]any{
			GameID: map[string]any{
				"id":         GameID,
				"name":       "RimGovernor",
				"launchMode": "DirectPath",
				"target":     filepath.Join(l.GameCopy, "RimWorldWin64.exe"),
				"workingDir": l.GameCopy,
				"args": []string{
					"-savedatafolder=" + profile, "-logFile", filepath.Join(l.Root, "Player.log"),
					"-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720",
					"-rimgovernor-pause-on-load",
				},
			},
		},
	}
	out, _ := json.MarshalIndent(config, "", "  ")
	return string(out) + "\n"
}

// RenderModsConfig is a ModsConfig.xml activating the core game, Harmony,
// RimBridgeServer and the native package, in load order.
func RenderModsConfig(version string) string {
	return fmt.Sprintf(`<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>%s</version>
  <activeMods>
    <li>ludeon.rimworld</li>
    <li>brrainz.harmony</li>
    <li>brrainz.rimbridgeserver</li>
    <li>%s</li>
  </activeMods>
  <knownExpansions />
</ModsConfigData>
`, version, na.NativePackage)
}

// minimalPrefs is enough of a Prefs.xml for RimWorld to launch and
// TrimPrefs to fill in; the game writes the rest on first run.
const minimalPrefs = `<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <langFolderName>English</langFolderName>
  <devMode>False</devMode>
  <runInBackground>True</runInBackground>
  <automaticPauseMode>MajorThreat</automaticPauseMode>
  <resetModsConfigOnCrash>False</resetModsConfigOnCrash>
</PrefsData>
`

// playerPrefs is the Prefs.xml of the player's own RimWorld profile on
// this machine, "" when the game has never been run here.
func playerPrefs() string {
	profile := os.Getenv("USERPROFILE")
	if profile == "" {
		return ""
	}
	path := filepath.Join(profile, "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "Prefs.xml")
	if isFile(path) {
		return path
	}
	return ""
}

// BuildScript is the mod build, relative to the checkout.
const BuildScript = "scripts/build_native_mod.ps1"

var validateSet = regexp.MustCompile(`\[ValidateSet\(([^)]*)\)\]`)
var quoted = regexp.MustCompile(`'([A-Za-z0-9_]+)'`)

// AllFixtures lists the fixture classes BuildScript's -Fixture accepts (its
// ValidateSet), sorted: what a build serving every case needs.
func AllFixtures(repo string) ([]string, error) {
	src, err := os.ReadFile(filepath.Join(repo, filepath.FromSlash(BuildScript)))
	if err != nil {
		return nil, err
	}
	return ParseValidateSet(string(src))
}

// ParseValidateSet extracts the quoted names of the first [ValidateSet(...)]
// in a PowerShell source.
func ParseValidateSet(src string) ([]string, error) {
	m := validateSet.FindStringSubmatch(src)
	if m == nil {
		return nil, errors.New("no [ValidateSet(...)] in " + BuildScript)
	}
	var out []string
	for _, q := range quoted.FindAllStringSubmatch(m[1], -1) {
		out = append(out, q[1])
	}
	sort.Strings(out)
	return out, nil
}

// alwaysFixtures are what BuildScript adds to any fixture build.
var alwaysFixtures = []string{"QuietStorytellerFixture", "DebugStartFixture", "LetterFixture", "FreezeNeedsFixture", "ShutdownFixture"}

// expectedFixtures is the manifest's fixture list a build with wanted
// would record.
func expectedFixtures(wanted []string) []string {
	if len(wanted) == 0 {
		return nil
	}
	seen := map[string]bool{}
	var out []string
	for _, f := range append(append([]string{}, wanted...), alwaysFixtures...) {
		if !seen[f] {
			seen[f] = true
			out = append(out, f)
		}
	}
	sort.Strings(out)
	return out
}

// installMod brings the copy's Mods/RimGovernor to a build of this
// worktree's native sources with the wanted fixtures: it skips when the
// installed manifest already records that source tree and fixture set,
// otherwise builds through BuildScript and installs the package -- never
// while a game runs from the copy.
func installMod(ctx context.Context, o Options, s *Summary, note func(string, ...any)) error {
	l := o.Layout
	wanted := o.Fixtures
	if !o.Production && wanted == nil {
		all, err := AllFixtures(l.Repo)
		if err != nil {
			return err
		}
		wanted = all
	}
	if o.Production {
		wanted = nil
	}
	s.Fixtures = expectedFixtures(wanted)
	installed := filepath.Join(l.GameCopy, "Mods", "RimGovernor")
	current, err := na.SourceTreeHash(l.Repo)
	if err != nil {
		return err
	}
	reason := "no installed build"
	if manifest, err := na.ReadPackageManifest(installed); err == nil {
		got := append([]string{}, manifest.Fixtures...)
		sort.Strings(got)
		switch {
		case o.Rebuild:
			reason = "-rebuild"
		case manifest.SourceTree != current:
			reason = "installed build's sources differ from the worktree"
		case strings.Join(got, ",") != strings.Join(s.Fixtures, ","):
			reason = fmt.Sprintf("installed build has fixtures %v, wanted %v", got, s.Fixtures)
		default:
			s.ModReason = "installed build is current"
			fmt.Fprintln(o.Log, "mod       "+s.ModReason)
			return nil
		}
	}
	s.ModReason = reason
	running, err := RunningGames(l.GameCopy)
	if err != nil {
		return err
	}
	if len(running) > 0 {
		return fmt.Errorf("a game is running from %s (pid %v); stop it first (%s stop -root %s) -- a DLL must never be replaced under a running game", l.GameCopy, running, filepath.Join(l.Bin, "acceptance.exe"), l.Root)
	}
	role := "fixture"
	if len(wanted) == 0 {
		role = "production"
	}
	output := filepath.Join(l.Builds, fmt.Sprintf("%s-%s", role, time.Now().Format("20060102-150405")))
	note("building the %s mod (%s) into %s", role, reason, output)
	if err := runBuildScript(ctx, l, o.Inputs, wanted, output, o.Log); err != nil {
		return err
	}
	s.ModBuilt, s.ModBuild = true, output
	pkg := filepath.Join(output, "RimGovernor")
	if _, err := na.ReadPackageManifest(pkg); err != nil {
		return fmt.Errorf("build produced no package: %w", err)
	}
	// The install is a rename away and a fresh copy in, so a half-copied
	// package never sits under the game's Mods.
	if isDir(installed) {
		trash := filepath.Join(l.Repo, ".rimgovernor", "trash", "RimGovernor-"+time.Now().Format("20060102-150405"))
		if err := os.MkdirAll(filepath.Dir(trash), 0755); err != nil {
			return err
		}
		if err := os.Rename(installed, trash); err != nil {
			return fmt.Errorf("move the old package aside: %w", err)
		}
		os.RemoveAll(trash)
	}
	if _, err := copyTree(pkg, installed); err != nil {
		return err
	}
	if err := na.RequireNativePackage(filepath.Join(l.GameCopy, "Mods")); err != nil {
		return err
	}
	note("installed %s (fixtures %v)", installed, s.Fixtures)
	return nil
}

// runBuildScript runs BuildScript in PowerShell (pwsh, else Windows
// PowerShell) with the discovered inputs, streaming its output to log.
func runBuildScript(ctx context.Context, l Layout, in *Inputs, fixtures []string, output string, log io.Writer) error {
	shell, err := exec.LookPath("pwsh")
	if err != nil {
		if shell, err = exec.LookPath("powershell"); err != nil {
			return errors.New("neither pwsh nor powershell is on PATH")
		}
	}
	q := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "''") + "'" }
	command := fmt.Sprintf("& %s -RimWorldManagedDir %s -HarmonyAssembly %s -RimBridgeSdkDir %s -OutputRoot %s",
		q(filepath.Join(l.Repo, filepath.FromSlash(BuildScript))), q(in.ManagedDir), q(in.Harmony), q(in.RimBridgeSDK), q(output))
	if len(fixtures) > 0 {
		var qs []string
		for _, f := range fixtures {
			qs = append(qs, q(f))
		}
		command += " -Fixture @(" + strings.Join(qs, ",") + ")"
	}
	command += "; exit $LASTEXITCODE"
	cmd := exec.CommandContext(ctx, shell, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command)
	cmd.Dir = l.Repo
	cmd.Stdout, cmd.Stderr = log, log
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("%s: %w", BuildScript, err)
	}
	return nil
}

// buildBinaries builds acceptance.exe and rimgovernor.exe into Bin. A
// binary that is running (this very setup, say) is renamed aside first:
// Windows lets a running image move but not be overwritten.
func buildBinaries(ctx context.Context, l Layout, s *Summary, note func(string, ...any)) error {
	if err := os.MkdirAll(l.Bin, 0755); err != nil {
		return err
	}
	for _, b := range []struct{ name, pkg string }{
		{"acceptance.exe", "./internal/nativeaccept/cmd/acceptance"},
		{"rimgovernor.exe", "./cmd/rimgovernor"},
	} {
		target := filepath.Join(l.Bin, b.name)
		fresh := target + ".new"
		cmd := exec.CommandContext(ctx, "go", "build", "-o", fresh, b.pkg)
		cmd.Dir = filepath.Join(l.Repo, "go")
		if out, err := cmd.CombinedOutput(); err != nil {
			return fmt.Errorf("go build %s: %v\n%s", b.pkg, err, out)
		}
		if isFile(target) {
			old := target + ".old"
			os.Remove(old)
			if err := os.Rename(target, old); err != nil {
				return fmt.Errorf("move %s aside (is a harness running from it?): %w", target, err)
			}
			os.Remove(old)
		}
		if err := os.Rename(fresh, target); err != nil {
			return err
		}
		s.Binaries = append(s.Binaries, target)
		note("built %s", target)
	}
	return nil
}
