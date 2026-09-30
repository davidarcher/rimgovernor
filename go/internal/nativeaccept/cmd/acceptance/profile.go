package main

// acceptance profile-capture (#1320): the loop for native snapshot
// performance work. It runs the test/profile_capture op (WasteFixture.cs),
// which calls SnapshotFrames.Capture back to back on the game thread,
// paused, and prints p50/p90/max per family and ObservationWork.Detail
// span whether or not a capture was slow. The game is the root's kept
// process (healed like a run's when the installed mod is stale), and the
// save is the newest sustained/colony checkpoint so the colony has real
// rows; the next call reloads it, so an edit, a rebuild and a profile take
// one command.

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const profileUsage = `
  acceptance profile-capture -root <dir> [-n <count> -equality -save <name> | -from <label|dir> -case <area/case> -output <dir> -game <id> -headless=false -timeout <d> -no-heal -json]
    loads the save (default: the -case ring's newest bundle, sustained/colony) on the kept game, runs SnapshotFrames.Capture
    -n times paused and prints p50/p90/max ms and rows per family and detail span; -equality adds the ColonyFacts equality probe`

const profileOp = "test/profile_capture"

// profileSave is the profile save the bundle is copied to, overwritten on
// every call so the loaded colony is the bundle's.
const profileSave = "RimGovernor-profile-capture"

type profileOptions struct {
	Count    int
	Equality bool
	Save     string
	Case     string
	From     string
	NoHeal   bool
	fixture  fixtureOptions
}

func parseProfile(args []string, stderr io.Writer) (profileOptions, error) {
	fs := flag.NewFlagSet("profile-capture", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var p profileOptions
	o := &p.fixture
	fs.StringVar(&o.Root, "root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	fs.IntVar(&p.Count, "n", 20, "captures to time (1..1000)")
	fs.BoolVar(&p.Equality, "equality", false, "also run the ColonyFacts optimizations off/on equality probe")
	fs.StringVar(&p.Save, "save", "", "profile save to load instead of a checkpoint bundle")
	fs.StringVar(&p.Case, "case", "sustained/colony", "case whose checkpoint ring supplies the save")
	fs.StringVar(&p.From, "from", "", "bundle: a ring label (t+7m, failed) or a bundle directory (default: the ring's newest entry, then its failed bundle)")
	fs.StringVar(&o.Output, "output", "", "evidence directory (default <root>/acceptance/fixture)")
	fs.StringVar(&o.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.BoolVar(&o.Headless, "headless", true, "use the headless profile (false: windowed)")
	fs.DurationVar(&o.Timeout, "timeout", 5*time.Minute, "safety net for the whole call")
	fs.BoolVar(&p.NoHeal, "no-heal", false, "refuse a stale or fixture-less installed mod instead of rebuilding it")
	fs.BoolVar(&o.JSON, "json", false, "print the stats as one JSON object")
	if err := fs.Parse(args); err != nil {
		return p, err
	}
	if len(fs.Args()) > 0 {
		return p, fmt.Errorf("unexpected arguments: %v", fs.Args())
	}
	if o.Root == "" {
		return p, errors.New("-root is required")
	}
	if !filepath.IsAbs(o.Root) {
		return p, fmt.Errorf("-root must be absolute: %s", o.Root)
	}
	if p.Count < 1 || p.Count > 1000 {
		return p, fmt.Errorf("-n must be 1..1000: %d", p.Count)
	}
	if p.Save != "" && p.From != "" {
		return p, errors.New("-save and -from are exclusive")
	}
	if o.Output == "" {
		o.Output = filepath.Join(o.Root, "acceptance", "fixture")
	}
	o.Output = mustAbs(o.Output)
	o.Op = profileOp
	o.Args = map[string]any{"count": p.Count, "equality": p.Equality}
	o.Save = p.Save
	return p, nil
}

// profileBundle resolves the checkpoint bundle holding the save: -from as
// a directory or a label of the case's ring, else the ring's newest entry,
// else its failed bundle.
func profileBundle(root, caseName, from string) (string, error) {
	if from != "" {
		if info, err := os.Stat(from); err == nil && info.IsDir() {
			return from, nil
		}
	}
	dir := cases.Options{Root: root}.RingDir(cases.Case{Name: caseName})
	ring, err := na.ReadRing(dir)
	if err != nil {
		return "", err
	}
	if from != "" {
		if ring == nil {
			return "", fmt.Errorf("no checkpoint ring at %s to take %q from", dir, from)
		}
		if from == "failed" && ring.Failed != nil {
			return ring.Failed.Path, nil
		}
		if e, ok := ring.Entry(from); ok {
			return e.Path, nil
		}
		return "", fmt.Errorf("%s: no bundle %q in the ring", dir, from)
	}
	if bundle := newestInRing(ring); bundle != "" {
		return bundle, nil
	}
	// A worktree's root is private, so a fresh one has no ring; borrow the
	// newest bundle a peer checkout's same root holds (read only: the save
	// is copied into this profile).
	if bundle := peerBundle(root, caseName); bundle != "" {
		return bundle, nil
	}
	return "", fmt.Errorf("no %s checkpoint in %s or any peer checkout: run `acceptance run %s` once, or pass -save", caseName, dir, caseName)
}

// newestInRing is the ring's newest entry, else its failed bundle, else "".
func newestInRing(ring *na.Ring) string {
	if ring == nil {
		return ""
	}
	if n := len(ring.Entries); n > 0 {
		return ring.Entries[n-1].Path
	}
	if ring.Failed != nil {
		return ring.Failed.Path
	}
	return ""
}

// peerBundle is the most recently written ring bundle of caseName among
// the main checkout and its .claude/worktrees, at root's path relative to
// its own checkout; "" when root is outside a checkout or none has one.
func peerBundle(root, caseName string) string {
	checkout, ok := na.FindRepo(root)
	if !ok {
		return ""
	}
	rel, err := filepath.Rel(checkout, root)
	if err != nil || strings.HasPrefix(rel, "..") {
		return ""
	}
	main := checkout
	if i := strings.Index(strings.ToLower(checkout), strings.ToLower(filepath.Join(".claude", "worktrees"))); i > 0 {
		main = filepath.Clean(checkout[:i])
	}
	peers, _ := filepath.Glob(filepath.Join(main, ".claude", "worktrees", "*"))
	var best string
	var newest time.Time
	for _, peer := range append([]string{main}, peers...) {
		if filepath.Clean(peer) == filepath.Clean(checkout) {
			continue
		}
		ring, _ := na.ReadRing(cases.Options{Root: filepath.Join(peer, rel)}.RingDir(cases.Case{Name: caseName}))
		bundle := newestInRing(ring)
		if bundle == "" {
			continue
		}
		info, err := os.Stat(filepath.Join(bundle, na.CheckpointSaveName(caseName)+".rws"))
		if err == nil && info.ModTime().After(newest) {
			best, newest = bundle, info.ModTime()
		}
	}
	return best
}

// stageProfileSave copies the bundle's save into the profile as
// profileSave, replacing the last call's copy.
func stageProfileSave(root, caseName, bundle string) error {
	data, err := os.ReadFile(filepath.Join(bundle, na.CheckpointSaveName(caseName)+".rws"))
	if err != nil {
		return err
	}
	target := filepath.Join(root, "profile", "Saves")
	if err := os.MkdirAll(target, 0755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(target, profileSave+".rws"), data, 0644)
}

func profileCapture(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	p, err := parseProfile(args, stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	o := p.fixture
	if o.Save == "" {
		bundle, err := profileBundle(o.Root, p.Case, p.From)
		if err == nil {
			err = stageProfileSave(o.Root, p.Case, bundle)
		}
		if err != nil {
			fmt.Fprintln(stderr, err)
			return 1
		}
		age := "?"
		if info, err := os.Stat(filepath.Join(bundle, na.CheckpointSaveName(p.Case)+".rws")); err == nil {
			age = time.Since(info.ModTime()).Round(time.Minute).String()
		}
		fmt.Fprintf(stdout, "profile-capture: %s from %s (saved %s ago)\n", p.Case, bundle, age)
		o.Save = profileSave
	}
	opts := cases.Options{Root: o.Root, Output: o.Output, GameID: o.GameID, Headless: o.Headless, NoHeal: p.NoHeal}
	if _, ok := preflight(ctx, []cases.Case{{Start: cases.Fixture{Op: profileOp}}}, opts, stdout); !ok {
		return 2
	}
	ctx, cancel := context.WithTimeout(ctx, o.Timeout)
	defer cancel()
	started := time.Now()
	result := fixtureResult{Op: o.Op, Args: o.Args}
	result.Output = filepath.Join(o.Output, "profile_capture-"+time.Now().Format("150405"))
	if err := executeFixture(ctx, o, &result); err != nil {
		result.Error = err.Error()
	}
	if result.Error != "" || !result.Success {
		printFixture(stdout, false, result)
		return 1
	}
	profile, err := summarizeProfile(result.Response)
	if err != nil {
		fmt.Fprintln(stdout, err)
		return 1
	}
	if o.JSON {
		data, _ := json.MarshalIndent(profile, "", "  ")
		fmt.Fprintln(stdout, string(data))
	} else {
		printProfile(stdout, profile)
		fmt.Fprintf(stdout, "profile-capture: %s in %s (%s)\n", o.Save, time.Since(started).Round(100*time.Millisecond), result.Output)
	}
	if profile.Equality != nil {
		if equal, _ := profile.Equality["equal"].(bool); !equal {
			return 1
		}
	}
	return 0
}

// profileStat is one span's distribution over the captures, in ms; Rows
// is the largest row count a capture reported.
type profileStat struct {
	Name   string  `json:"name"`
	Detail bool    `json:"detail,omitempty"`
	P50    float64 `json:"p50"`
	P90    float64 `json:"p90"`
	Max    float64 `json:"max"`
	Rows   int64   `json:"rows"`
	Seen   int     `json:"seen"`
}

type profileSummary struct {
	Count      int            `json:"count"`
	Tick       int64          `json:"tick"`
	FrameBytes int64          `json:"frameBytes"`
	Pawns      int64          `json:"pawns"`
	Things     int64          `json:"things"`
	Total      profileStat    `json:"total"`
	Families   []profileStat  `json:"families"`
	Details    []profileStat  `json:"details"`
	Equality   map[string]any `json:"equality,omitempty"`
}

// summarizeProfile reduces the op's raw captures to per-span stats,
// families and details each ordered by p50, slowest first.
func summarizeProfile(reply map[string]any) (profileSummary, error) {
	raw, _ := reply["captures"].([]any)
	if len(raw) == 0 {
		return profileSummary{}, fmt.Errorf("%s returned no captures: %v", profileOp, reply)
	}
	s := profileSummary{Count: len(raw), Tick: int64(na.AsNumber(reply["tick"])), FrameBytes: int64(na.AsNumber(reply["frameBytes"])),
		Pawns: int64(na.AsNumber(reply["pawns"])), Things: int64(na.AsNumber(reply["things"]))}
	s.Equality, _ = reply["equality"].(map[string]any)
	var totals []float64
	samples := map[string][]float64{}
	rows := map[string]int64{}
	detail := map[string]bool{}
	var order []string
	for _, c := range raw {
		capture, _ := c.(map[string]any)
		totals = append(totals, na.AsNumber(capture["totalMs"]))
		sections, _ := capture["sections"].([]any)
		for _, x := range sections {
			section, _ := x.(map[string]any)
			name, _ := section["name"].(string)
			if _, ok := samples[name]; !ok {
				order = append(order, name)
			}
			samples[name] = append(samples[name], na.AsNumber(section["ms"]))
			rows[name] = max(rows[name], int64(na.AsNumber(section["rows"])))
			detail[name], _ = section["detail"].(bool)
		}
	}
	s.Total = stat("total", totals)
	for _, name := range order {
		st := stat(name, samples[name])
		st.Rows, st.Detail = rows[name], detail[name]
		if st.Detail {
			s.Details = append(s.Details, st)
		} else {
			s.Families = append(s.Families, st)
		}
	}
	for _, list := range [][]profileStat{s.Families, s.Details} {
		sort.SliceStable(list, func(i, j int) bool { return list[i].P50 > list[j].P50 })
	}
	return s, nil
}

// stat is the nearest-rank p50/p90 and the max of values.
func stat(name string, values []float64) profileStat {
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	rank := func(p float64) float64 {
		i := int(math.Ceil(p*float64(len(sorted)))) - 1
		return sorted[max(i, 0)]
	}
	return profileStat{Name: name, P50: rank(0.5), P90: rank(0.9), Max: sorted[len(sorted)-1], Seen: len(sorted)}
}

func printProfile(w io.Writer, s profileSummary) {
	fmt.Fprintf(w, "%d captures at tick %d: %d pawns, %d things, frame %d bytes\n", s.Count, s.Tick, s.Pawns, s.Things, s.FrameBytes)
	fmt.Fprintf(w, "%-32s %9s %9s %9s %8s\n", "span", "p50 ms", "p90 ms", "max ms", "rows")
	line := func(st profileStat, indent string) {
		fmt.Fprintf(w, "%-32s %9.2f %9.2f %9.2f %8d\n", indent+st.Name, st.P50, st.P90, st.Max, st.Rows)
	}
	line(s.Total, "")
	for _, st := range s.Families {
		line(st, "  ")
	}
	if len(s.Details) > 0 {
		fmt.Fprintln(w, "detail spans:")
		for _, st := range s.Details {
			line(st, "  ")
		}
	}
	if s.Equality != nil {
		data, _ := json.Marshal(s.Equality)
		fmt.Fprintf(w, "equality: %s\n", strings.TrimSpace(string(data)))
	}
}
