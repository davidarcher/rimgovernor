// Package doctor is the acceptance preflight (#277): every environment
// pitfall that has cost a run ten minutes before it surfaced, checked in
// a few seconds against a bridge root. Each check names its fix rather
// than refusing; only a check that would certainly fail a run is Fail,
// the rest Warn (a relaunch, a slow first poll, a stale binary).
package doctor

import (
	"context"
	"database/sql"
	"fmt"
	"io"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime/debug"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Status is a check's verdict.
type Status int

const (
	// OK: nothing to do.
	OK Status = iota
	// Warn: the run starts but pays for it (a relaunch, a slow first
	// poll) or its evidence is suspect (a binary behind main).
	Warn
	// Fail: the run cannot reach its first tick.
	Fail
)

func (s Status) String() string {
	switch s {
	case OK:
		return "ok"
	case Warn:
		return "warn"
	default:
		return "FAIL"
	}
}

// Check is one line of the report.
type Check struct {
	Name   string
	Status Status
	// Detail is what was found; Fix is what to do about it (empty on OK).
	Detail string
	Fix    string
}

// Options name what a run would use.
type Options struct {
	// Root is the bridge root (`-root`), absolute.
	Root string
	// Rimgovernor is the serve binary (`-rimgovernor`), "" when the run
	// is bridge-only.
	Rimgovernor string
	// Output is the run's output directory ("" for the root's default);
	// Cases are the registry names the run would write under it.
	Output string
	Cases  []string
	// GameID is the configured game (setup.GameID by default).
	GameID string
	// Repo is the checkout the stale-package check compares against: the
	// one enclosing the working directory (what Prepare uses), else the
	// one enclosing Root.
	Repo string
}

// ClockJournalBacklog is how many journal rows under a kept process warn:
// a service starting from cursor 1 pages every one before it sees a live
// event, and the first review's budget goes with them (#119).
const ClockJournalBacklog = 2000

// Run performs every check and returns them in report order.
func Run(ctx context.Context, o Options) []Check {
	if o.GameID == "" {
		o.GameID = setup.GameID
	}
	o.Root = mustAbs(o.Root)
	if o.Repo == "" {
		if cwd, err := os.Getwd(); err == nil {
			o.Repo, _ = na.FindRepo(cwd)
		}
		if o.Repo == "" {
			o.Repo, _ = na.FindRepo(o.Root)
		}
	}
	var checks []Check
	add := func(c Check) { checks = append(checks, c) }

	game, gameCopy, c := root(o)
	add(c)
	if game != nil {
		add(gameCopyPath(gameCopy))
		add(gabs(o))
		add(mod(o, gameCopy))
	}
	add(baseline(o))
	add(modsConfig(o))
	var running []int
	if gameCopy != "" {
		var c Check
		running, c = process(o, gameCopy)
		add(c)
		add(journal(o, len(running) > 0))
	}
	add(gocache())
	add(runBinary(ctx, o))
	if o.Rimgovernor != "" {
		add(serveBinary(ctx, o))
	}
	add(output(o))
	return checks
}

// Failed reports whether any check is Fail.
func Failed(checks []Check) bool {
	for _, c := range checks {
		if c.Status == Fail {
			return true
		}
	}
	return false
}

// Write prints one line per check (only Warn and Fail when onlyFailing),
// the fix indented under it.
func Write(w io.Writer, checks []Check, onlyFailing bool) {
	for _, c := range checks {
		if onlyFailing && c.Status == OK {
			continue
		}
		fmt.Fprintf(w, "%-5s %-14s %s\n", c.Status, c.Name, c.Detail)
		if c.Fix != "" {
			fmt.Fprintf(w, "      fix: %s\n", c.Fix)
		}
	}
}

// setupHint is the fix for anything setup makes, with any extra flags.
func setupHint(repo string, flags ...string) string {
	cmd := "go run ./internal/nativeaccept/cmd/acceptance setup"
	if repo != "" {
		cmd += " -worktree " + repo
	}
	if len(flags) > 0 {
		cmd += " " + strings.Join(flags, " ")
	}
	return cmd + " (from go/)"
}

// root reads the root's config.json and the game it launches; the game
// section and its workingDir (the game copy) feed the later checks.
func root(o Options) (map[string]any, string, Check) {
	c := Check{Name: "root", Detail: o.Root}
	configPath := filepath.Join(o.Root, "config", "config.json")
	cfg := &na.Config{Root: o.Root, GameID: o.GameID, Configuration: filepath.Join(o.Root, "config")}
	if _, err := os.Stat(configPath); err != nil {
		c.Status, c.Detail, c.Fix = Fail, "no config/config.json under "+o.Root, setupHint(o.Repo)
		return nil, "", c
	}
	game, err := cfg.GameSection()
	if err != nil {
		c.Status, c.Detail, c.Fix = Fail, configPath+": "+err.Error(), setupHint(o.Repo)
		return nil, "", c
	}
	workingDir, _ := game["workingDir"].(string)
	target, _ := game["target"].(string)
	if workingDir == "" || target == "" {
		c.Status, c.Detail, c.Fix = Fail, configPath+": the game has no DirectPath target or workingDir", setupHint(o.Repo)
		return nil, "", c
	}
	if _, err := os.Stat(target); err != nil {
		c.Status, c.Detail, c.Fix = Fail, "game exe missing: "+target, setupHint(o.Repo)
		return game, workingDir, c
	}
	c.Detail = fmt.Sprintf("%s -> %s", o.Root, workingDir)
	return game, workingDir, c
}

// gameCopyPath is setup's MAX_PATH bound: a copy past it boots but cannot
// open any Defs XML.
func gameCopyPath(gameCopy string) Check {
	c := Check{Name: "game-copy", Detail: fmt.Sprintf("%d chars: %s", len(gameCopy), gameCopy)}
	if len(gameCopy) > setup.MaxGameCopyPath {
		c.Status = Fail
		c.Detail = fmt.Sprintf("path is %d characters (limit %d): RimWorld cannot open its own Defs XML from it", len(gameCopy), setup.MaxGameCopyPath)
		c.Fix = fmt.Sprintf("move the worktree under a path at most %d characters (e.g. .claude/worktrees/<name>) and rerun setup", setup.MaxGameCopyPath-len(filepath.FromSlash("/.rimgovernor/native-rimworld")))
	}
	return c
}

func gabs(o Options) Check {
	c := Check{Name: "gabs"}
	path, err := na.GABSExecutable(o.Root, "")
	if err == nil {
		_, err = os.Stat(path)
	}
	if err != nil {
		c.Status, c.Detail, c.Fix = Fail, err.Error(), setupHint(o.Repo)
		return c
	}
	c.Detail = path
	return c
}

// mod is Prepare's own package checks (RequireNativePackage, then the
// stale-build comparison) run early, with the manifest's role and
// fixtures on the OK line.
func mod(o Options, gameCopy string) Check {
	c := Check{Name: "mod"}
	mods := filepath.Join(gameCopy, "Mods")
	if err := na.RequireNativePackage(mods); err != nil {
		c.Status, c.Detail, c.Fix = Fail, err.Error(), setupHint(o.Repo)
		return c
	}
	pkg := filepath.Join(mods, "RimGovernor")
	manifest, err := na.ReadPackageManifest(pkg)
	if err != nil {
		c.Status, c.Detail, c.Fix = Warn, "installed package has no "+na.PackageManifestName+"; the stale check cannot run", "rebuild through setup so the manifest records the sources"
		return c
	}
	role := manifest.Role
	if role == "" {
		role = "unknown role"
	}
	c.Detail = fmt.Sprintf("%s build from %s, %d fixtures", role, short(manifest.SourceRevision), len(manifest.Fixtures))
	if manifest.SourceDirty {
		c.Detail += " (dirty tree)"
	}
	// RequireCurrentPackage compares against the checkout enclosing the
	// working directory, exactly as Prepare will.
	summary, err := na.RequireCurrentPackage(pkg)
	if err != nil {
		// The error's rebuild recipe is the by-hand one; setup is the fix.
		why, _, _ := strings.Cut(err.Error(), "; rebuild it")
		c.Status, c.Detail, c.Fix = Fail, why, setupHint(o.Repo)+", -rebuild if it says the build is current"
		return c
	}
	if skipped, _ := summary["skipped"].(string); skipped != "" {
		c.Status = Warn
		c.Detail += "; stale check skipped: " + skipped
		if os.Getenv(na.AllowStaleModEnv) != "" {
			c.Fix = "unset " + na.AllowStaleModEnv + " unless you mean to run against an older native build"
		}
	}
	return c
}

// baseline is the committed Core-only baseline save (#192): missing in
// both the checkout and the root, no save-driven case can start; a DLC
// entry in its modIds fails save.missing_mods on the Core-only profile.
func baseline(o Options) Check {
	c := Check{Name: "baseline"}
	rooted := filepath.Join(o.Root, "profile", "Saves", na.BaselineSave)
	committed := ""
	if o.Repo != "" {
		committed = filepath.Join(o.Repo, filepath.FromSlash(na.CommittedSavesDir), na.BaselineSave)
		if _, err := os.Stat(committed); err != nil {
			committed = ""
		}
	}
	_, rootErr := os.Stat(rooted)
	switch {
	case committed == "" && rootErr != nil:
		c.Status, c.Detail = Fail, "no "+na.BaselineSave+" in the root or the checkout"
		c.Fix = "commit it under " + na.CommittedSavesDir + " or copy a peer worktree's into " + filepath.Dir(rooted)
		return c
	case committed == "":
		c.Detail = rooted + " (root outside a checkout; not restaged)"
	case rootErr != nil:
		c.Detail = "not staged yet; Prepare copies " + committed
	default:
		same, _ := sameContent(committed, rooted)
		if same {
			c.Detail = rooted + " matches the committed save"
		} else {
			c.Detail = rooted + " differs from the committed save; Prepare replaces it"
		}
	}
	effective := committed
	if effective == "" {
		effective = rooted
	}
	mods, err := saveModIDs(effective)
	if err != nil {
		c.Status, c.Detail, c.Fix = Fail, effective+": "+err.Error(), "regenerate the baseline (variantsavegen, Core-only) or restore it from git"
		return c
	}
	var dlc []string
	for _, id := range mods {
		if strings.HasPrefix(id, na.ExpansionPrefix) {
			dlc = append(dlc, id)
		}
	}
	if len(dlc) > 0 {
		c.Status, c.Detail = Fail, fmt.Sprintf("%s was saved with %v; the Core-only profile fails save.missing_mods at load", effective, dlc)
		c.Fix = "use the committed Core-only baseline (git checkout -- " + na.CommittedSavesDir + ") or regenerate it Core-only"
	}
	return c
}

// saveModIDs reads the modIds of an .rws from its header (the first few
// KB of a multi-MB file).
func saveModIDs(path string) ([]string, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	head := make([]byte, 64<<10)
	n, _ := io.ReadFull(f, head)
	text := string(head[:n])
	start := strings.Index(text, "<modIds>")
	end := strings.Index(text, "</modIds>")
	if start < 0 || end < start {
		return nil, fmt.Errorf("no <modIds> in the save header")
	}
	var ids []string
	for _, part := range strings.Split(text[start+len("<modIds>"):end], "<li>") {
		if i := strings.Index(part, "</li>"); i >= 0 {
			ids = append(ids, strings.ToLower(strings.TrimSpace(part[:i])))
		}
	}
	return ids, nil
}

// modsConfig is the profile's ModsConfig.xml Prepare rewrites: it must
// exist and parse with a core entry; and a kept process launched with a
// different list or package is relaunched by the next run (#166, #209).
func modsConfig(o Options) Check {
	c := Check{Name: "mods-config"}
	path := filepath.Join(o.Root, "profile", "Config", "ModsConfig.xml")
	active, err := na.ActiveMods(path)
	if err != nil {
		c.Status, c.Detail, c.Fix = Fail, err.Error(), setupHint(o.Repo)
		return c
	}
	hasCore := false
	for _, id := range active {
		if id == na.CorePackage {
			hasCore = true
		}
	}
	if !hasCore {
		c.Status, c.Detail = Fail, path+" activates no "+na.CorePackage+"; Prepare refuses it"
		c.Fix = "delete it and rerun setup (it rewrites the profile's ModsConfig.xml)"
		return c
	}
	c.Detail = fmt.Sprintf("%d active mods", len(active))
	headless := filepath.Join(o.Root, "config-headless")
	if _, err := os.Stat(filepath.Join(headless, "config.json")); err != nil {
		return c
	}
	reason, err := na.LaunchedMismatch(headless)
	if err != nil || reason == "" {
		return c
	}
	c.Status = Warn
	switch reason {
	case "unrecorded":
		c.Detail += "; the last launch recorded no mod list or package snapshot"
	case "expansions":
		c.Detail += "; the prepared list differs from what the kept process launched with"
	default:
		c.Detail += "; the installed package differs from what the kept process launched with"
	}
	c.Fix = "the next run relaunches the game (one boot); `acceptance stop -root " + o.Root + "` to pay it now"
	return c
}

// process lists this root's own RimWorld processes (setup.RunningGames:
// executables under the game copy, never a peer's). One with a launch
// record is the kept process a run reuses; two is a leak.
func process(o Options, gameCopy string) ([]int, Check) {
	c := Check{Name: "game"}
	pids, err := setup.RunningGames(gameCopy)
	if err != nil {
		c.Status, c.Detail = Warn, "could not list RimWorld processes: "+err.Error()
		return nil, c
	}
	switch len(pids) {
	case 0:
		c.Detail = "no game running from the copy; the first case boots one"
	case 1:
		c.Detail = fmt.Sprintf("kept process pid %d running from the copy", pids[0])
	default:
		c.Status, c.Detail = Warn, fmt.Sprintf("%d games run from the copy (pids %v); GABS reuses one and the rest are leaks", len(pids), pids)
		c.Fix = "stop the extras by pid (Stop-Process -Id <pid>), never by image name"
	}
	return pids, c
}

// journal is the clock journal backlog of the headless profile: a fresh
// launch clears it, a kept process must keep it (#119), so a large one
// under a running game is a slow first review.
func journal(o Options, running bool) Check {
	c := Check{Name: "clock-journal"}
	dir, err := na.ClockJournalDir(filepath.Join(o.Root, "config-headless"))
	if err != nil {
		c.Detail = "no headless profile yet"
		return c
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		c.Detail = "empty"
		return c
	}
	n := len(entries)
	switch {
	case !running:
		c.Detail = fmt.Sprintf("%d rows; cleared at the next fresh launch", n)
	case n > ClockJournalBacklog:
		c.Status, c.Detail = Warn, fmt.Sprintf("%d rows under the kept process; a service pages every one before its first live event", n)
		c.Fix = "`acceptance stop -root " + o.Root + "` so the next launch starts empty; never delete the journal under a running game (#119)"
	default:
		c.Detail = fmt.Sprintf("%d rows under the kept process", n)
	}
	return c
}

// gocache warns on a private build cache (AGENTS.md: the shared default
// is what makes cmd/test and the setup builds fast).
func gocache() Check {
	c := Check{Name: "gocache"}
	out, err := exec.Command("go", "env", "GOCACHE").Output()
	if err != nil {
		c.Detail = "go env failed: " + err.Error()
		return c
	}
	current := filepath.Clean(strings.TrimSpace(string(out)))
	local := os.Getenv("LOCALAPPDATA")
	if local == "" {
		c.Detail = current
		return c
	}
	shared := filepath.Join(local, "go-build")
	if !strings.EqualFold(current, shared) {
		c.Status, c.Detail = Warn, current+" is not the shared "+shared
		c.Fix = "unset GOCACHE (Remove-Item Env:GOCACHE; `go env -u GOCACHE`): a private cache defeats the test cache and peers' builds"
		return c
	}
	c.Detail = current
	return c
}

// runBinary is this very binary against the checkout: a run binary behind
// main reproduces failures main already fixed (#212). A `go run` build
// carries no VCS stamp and is by construction the worktree's HEAD.
func runBinary(ctx context.Context, o Options) Check {
	c := Check{Name: "acceptance"}
	rev, dirty := buildRevision()
	if rev == "" {
		c.Detail = "no VCS stamp (go run, or built outside a checkout)"
		return c
	}
	return behindMain(ctx, c, o.Repo, rev, dirty)
}

// serveBinary is the -rimgovernor binary: it must exist and, like the
// runner, should not trail main.
func serveBinary(ctx context.Context, o Options) Check {
	c := Check{Name: "rimgovernor"}
	if _, err := os.Stat(o.Rimgovernor); err != nil {
		c.Status, c.Detail, c.Fix = Fail, err.Error(), setupHint(o.Repo, "-skip-mod")+" builds .rimgovernor/bin/rimgovernor.exe"
		return c
	}
	out, err := exec.CommandContext(ctx, "go", "version", "-m", o.Rimgovernor).Output()
	if err != nil {
		c.Detail = o.Rimgovernor + " (build info unreadable)"
		return c
	}
	rev, dirty := "", false
	for _, line := range strings.Split(string(out), "\n") {
		fields := strings.Fields(line)
		if len(fields) == 2 && fields[0] == "build" {
			switch {
			case strings.HasPrefix(fields[1], "vcs.revision="):
				rev = strings.TrimPrefix(fields[1], "vcs.revision=")
			case fields[1] == "vcs.modified=true":
				dirty = true
			}
		}
	}
	if rev == "" {
		c.Detail = o.Rimgovernor + " (no VCS stamp)"
		return c
	}
	return behindMain(ctx, c, o.Repo, rev, dirty)
}

// behindMain fills c from rev's standing against the checkout's HEAD and
// main.
func behindMain(ctx context.Context, c Check, repo, rev string, dirty bool) Check {
	c.Detail = "built from " + short(rev)
	if dirty {
		c.Detail += " (dirty tree)"
	}
	if repo == "" {
		c.Detail += "; no checkout to compare against"
		return c
	}
	gitCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	git := func(args ...string) (string, error) {
		out, err := exec.CommandContext(gitCtx, "git", append([]string{"-C", repo}, args...)...).Output()
		return strings.TrimSpace(string(out)), err
	}
	head, err := git("rev-parse", "HEAD")
	if err != nil {
		c.Detail += "; git could not resolve HEAD"
		return c
	}
	if head != rev {
		c.Status = Warn
		c.Detail += ", the worktree is at " + short(head)
		c.Fix = "rebuild the binaries so the run reflects the checkout: " + setupHint(repo, "-skip-mod")
	}
	if _, err := git("cat-file", "-e", rev+"^{commit}"); err != nil {
		c.Status = Warn
		c.Detail += "; that revision is not in this checkout"
		return c
	}
	if _, err := git("merge-base", "--is-ancestor", rev, "main"); err != nil {
		c.Detail += "; not on main (branch build)"
		return c
	}
	log, err := git("log", "--oneline", rev+"..main")
	if err != nil || log == "" {
		return c
	}
	n := len(strings.Split(log, "\n"))
	c.Status = Warn
	c.Detail += fmt.Sprintf("; %d landings on main since", n)
	if c.Fix == "" {
		c.Fix = "git merge main, then " + setupHint(repo) + " before diagnosing anything the run fails"
	}
	return c
}

// buildRevision is this binary's VCS stamp.
func buildRevision() (rev string, dirty bool) {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return "", false
	}
	for _, s := range info.Settings {
		switch s.Key {
		case "vcs.revision":
			rev = s.Value
		case "vcs.modified":
			dirty = s.Value == "true"
		}
	}
	return rev, dirty
}

// output is the run's output directory: a case whose directory already
// holds anything is refused (every run needs a fresh one), and a
// service.sqlite there from an older schema is unreadable except raw
// (acceptance why).
func output(o Options) Check {
	c := Check{Name: "output"}
	dir := o.Output
	if dir == "" {
		dir = filepath.Join(o.Root, "acceptance")
	}
	dir = mustAbs(dir)
	var occupied []string
	for _, name := range o.Cases {
		if entries, _ := os.ReadDir(filepath.Join(dir, filepath.FromSlash(name))); len(entries) > 0 {
			occupied = append(occupied, name)
		}
	}
	if len(occupied) > 0 {
		c.Status, c.Detail = Fail, fmt.Sprintf("%s already holds %v; a case run into a non-empty directory is refused", dir, occupied)
		c.Fix = "pass a fresh -output (e.g. " + filepath.Join(filepath.Dir(dir), "runs", time.Now().Format("20060102-150405")) + ")"
		return c
	}
	results, _ := filepath.Glob(filepath.Join(dir, "*", "*", "result.json"))
	if len(results) == 0 {
		c.Detail = dir + " (fresh)"
		return c
	}
	c.Detail = fmt.Sprintf("%s holds %d earlier case results", dir, len(results))
	stale := 0
	for _, r := range results {
		if v, ok := storeVersion(filepath.Join(filepath.Dir(r), "service.sqlite")); ok && v != store.SchemaVersion() {
			stale++
		}
	}
	if stale > 0 {
		c.Status = Warn
		c.Detail += fmt.Sprintf("; %d service.sqlite of an older schema", stale)
		c.Fix = "read them with `acceptance why <case dir>` (raw), not store.Open; a new run writes the current schema"
	}
	return c
}

// storeVersion is a service.sqlite's user_version, false when absent or
// unreadable.
func storeVersion(path string) (int, bool) {
	if _, err := os.Stat(path); err != nil {
		return 0, false
	}
	uriPath := filepath.ToSlash(mustAbs(path))
	if !strings.HasPrefix(uriPath, "/") {
		uriPath = "/" + uriPath
	}
	u := &url.URL{Scheme: "file", Path: uriPath, RawQuery: "mode=ro&_busy_timeout=2000"}
	db, err := sql.Open(store.DriverName, u.String())
	if err != nil {
		return 0, false
	}
	defer db.Close()
	var version int
	if err := db.QueryRow("PRAGMA user_version").Scan(&version); err != nil {
		return 0, false
	}
	return version, true
}

// sameContent is whether both files hold the same bytes (sizes first).
func sameContent(a, b string) (bool, error) {
	ia, err := os.Stat(a)
	if err != nil {
		return false, err
	}
	ib, err := os.Stat(b)
	if err != nil {
		return false, err
	}
	if ia.Size() != ib.Size() {
		return false, nil
	}
	da, err := os.ReadFile(a)
	if err != nil {
		return false, err
	}
	db, err := os.ReadFile(b)
	if err != nil {
		return false, err
	}
	return string(da) == string(db), nil
}

func short(rev string) string {
	if len(rev) > 8 {
		return rev[:8]
	}
	if rev == "" {
		return "unknown"
	}
	return rev
}

func mustAbs(path string) string {
	abs, err := filepath.Abs(path)
	if err != nil {
		return path
	}
	return abs
}
