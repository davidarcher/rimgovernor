package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const fixtureUsage = `
  acceptance fixture <op> [key=value ...] -root <dir> [-save <name> | -loaded] [-output <dir> -game <id> -headless=false -timeout <d> -json]`

// fixtureOptions are the parsed fixture flags: the op, its arguments and
// the world it runs on.
type fixtureOptions struct {
	Op   string
	Args map[string]any
	// Save is the save the op runs on (the committed tribal8 baseline by
	// default); Loaded runs it on whatever an earlier call left loaded.
	Save     string
	Loaded   bool
	Root     string
	Output   string
	GameID   string
	Headless bool
	Timeout  time.Duration
	JSON     bool
}

// parseFixture resolves the fixture subcommand: the op name, its key=value
// arguments (a value that parses as JSON is that value, so x=12 is a
// number, roofed=true a bool and cells=[[1,2]] a list; anything else is a
// string), then the flags. Arguments precede the flags the way case names
// precede run's.
func parseFixture(args []string, stderr io.Writer) (fixtureOptions, error) {
	var positional, flagArgs []string
	for i, a := range args {
		if strings.HasPrefix(a, "-") {
			flagArgs = args[i:]
			break
		}
		positional = append(positional, a)
	}
	fs := flag.NewFlagSet("fixture", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var o fixtureOptions
	fs.StringVar(&o.Root, "root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	fs.StringVar(&o.Save, "save", strings.TrimSuffix(na.BaselineSave, ".rws"), "save to load before the op (profile/Saves, staged from "+cases.CommittedSavesDir+" when the root lacks it)")
	fs.BoolVar(&o.Loaded, "loaded", false, "run the op on the game an earlier fixture call left loaded instead of loading -save")
	fs.StringVar(&o.Output, "output", "", "evidence directory; each call writes under <output>/<op>-<time> (default <root>/acceptance/fixture)")
	fs.StringVar(&o.GameID, "game", "rimgovernor-trial", "configured game ID")
	fs.BoolVar(&o.Headless, "headless", true, "use the headless profile (false: windowed)")
	fs.DurationVar(&o.Timeout, "timeout", 10*time.Minute, "safety net for the whole call")
	fs.BoolVar(&o.JSON, "json", false, "print the response and census as one JSON object")
	if err := fs.Parse(flagArgs); err != nil {
		return o, err
	}
	if len(fs.Args()) > 0 {
		return o, fmt.Errorf("the op and its arguments must precede the flags: %v", fs.Args())
	}
	if len(positional) == 0 {
		return o, errors.New("fixture needs an op name (e.g. test/routes_prepare)")
	}
	o.Op = positional[0]
	o.Args = map[string]any{}
	for _, kv := range positional[1:] {
		key, value, ok := strings.Cut(kv, "=")
		if !ok || key == "" {
			return o, fmt.Errorf("argument %q is not key=value", kv)
		}
		o.Args[key] = fixtureValue(value)
	}
	if o.Root == "" {
		return o, errors.New("-root is required")
	}
	if !filepath.IsAbs(o.Root) {
		return o, fmt.Errorf("-root must be absolute: %s", o.Root)
	}
	saveSet := false
	fs.Visit(func(f *flag.Flag) { saveSet = saveSet || f.Name == "save" })
	if o.Loaded && saveSet {
		return o, errors.New("-loaded and -save are exclusive")
	}
	if o.Output == "" {
		o.Output = filepath.Join(o.Root, "acceptance", "fixture")
	}
	o.Output = mustAbs(o.Output)
	return o, nil
}

// fixtureValue decodes one key=value argument's value: JSON when it parses
// as JSON, the literal string otherwise.
func fixtureValue(value string) any {
	var decoded any
	if err := json.Unmarshal([]byte(value), &decoded); err == nil {
		return decoded
	}
	return value
}

// fixtureResult is what one fixture call prints: the op's reply as native
// returned it (a refusal included) and the world census after it.
type fixtureResult struct {
	Op       string         `json:"op"`
	Args     map[string]any `json:"args"`
	Start    map[string]any `json:"start,omitempty"`
	Response map[string]any `json:"response"`
	Success  bool           `json:"success"`
	Census   *fixtureCensus `json:"census,omitempty"`
	Output   string         `json:"output"`
	Error    string         `json:"error,omitempty"`
}

// fixtureCensus is the CheckReset sample (na.ObserveReset) in the shape the
// command prints.
type fixtureCensus struct {
	ColonyID        string                    `json:"colonyId"`
	LoadToken       string                    `json:"loadToken"`
	Tick            float64                   `json:"tick"`
	Paused          bool                      `json:"paused"`
	AuthorityActive bool                      `json:"authorityActive"`
	OwnedDrafts     int                       `json:"ownedDrafts"`
	Stock           map[string]na.StockCounts `json:"stock"`
}

// runFixture opens the root's game (a kept process is attached, else one
// launches), loads the save (or keeps the loaded world under -loaded),
// calls the op through the same harness the cases use and prints its
// reply with a census of the world after it. The game stays loaded and
// paused with the process kept, so the next call (-loaded) continues on
// it; the next `acceptance run` unloads it as it would any leftover. The
// exit code is 1 when the call failed or native refused the op.
func runFixture(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	o, err := parseFixture(args, stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	ctx, cancel := context.WithTimeout(ctx, o.Timeout)
	defer cancel()
	result := fixtureResult{Op: o.Op, Args: o.Args}
	result.Output = filepath.Join(o.Output, strings.ReplaceAll(o.Op, "/", "_")+"-"+time.Now().Format("150405"))
	err = executeFixture(ctx, o, &result)
	if err != nil {
		result.Error = err.Error()
	}
	printFixture(stdout, o.JSON, result)
	if err != nil || !result.Success {
		return 1
	}
	return 0
}

func executeFixture(ctx context.Context, o fixtureOptions, result *fixtureResult) error {
	if err := os.MkdirAll(result.Output, 0755); err != nil {
		return err
	}
	var start na.Start = na.Loaded{}
	if !o.Loaded {
		// A save the profile lacks is staged from the committed checkpoints;
		// one it holds is used as is.
		if err := cases.StageSaves(cases.Save{Name: o.Save, From: cases.CommittedSaves()}, o.Root); err != nil {
			return err
		}
		start = na.Save{Name: o.Save}
	}
	// FixtureOps names the op so the stale-package check's rebuild hint
	// does; Prepare stages the baseline save into the profile.
	cfg := &na.Config{Root: o.Root, Output: result.Output, Headless: o.Headless, GameID: o.GameID,
		FixtureOps: []string{o.Op}, KeepLoaded: true}
	report := na.NewReport("fixture "+o.Op, o.Headless)
	session, err := na.OpenSession(ctx, cfg, report, start, na.QuietIfAvailable)
	if err != nil {
		return err
	}
	// The world stays loaded for the next call whatever the environment
	// says about keeping the process.
	session.Game.Keep = true
	defer func() {
		session.Close()
		report.Write(result.Output)
	}()
	result.Start, _ = report["start"].(map[string]any)
	if !na.Contains(session.Names, o.Op) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with its fixture%s", o.Op, na.FixtureBuildHint(o.Op))
	}
	reply, err := session.Harness.Call(ctx, "fixture", o.Op, o.Args)
	if err != nil {
		return err
	}
	result.Response = reply
	result.Success, _ = na.AsBool(reply["success"])
	report["prepared"] = reply
	// The op may have moved the clock; the census reads the paused world.
	if err := session.Pause(ctx); err != nil {
		return err
	}
	state, _, err := na.ObserveReset(ctx, session.Harness, "census")
	if err != nil {
		return fmt.Errorf("census: %w", err)
	}
	result.Census = &fixtureCensus{ColonyID: state.ColonyID, LoadToken: state.LoadToken, Tick: state.Tick, Paused: state.Paused,
		AuthorityActive: state.AuthorityActive, OwnedDrafts: state.OwnedDrafts, Stock: state.Stock}
	report["census"] = result.Census
	report["passed"] = result.Success
	return nil
}

// printFixture writes the result: one JSON object, or the response block
// followed by a short census (tick, pause, authority, drafts and the
// non-zero stocks in name order).
func printFixture(w io.Writer, asJSON bool, r fixtureResult) {
	if asJSON {
		data, _ := json.MarshalIndent(r, "", "  ")
		fmt.Fprintln(w, string(data))
		return
	}
	status := "OK"
	if !r.Success {
		status = "REFUSED"
	}
	if r.Error != "" {
		status = "ERROR"
	}
	fmt.Fprintf(w, "%s\t%s\t%s\n", status, r.Op, r.Output)
	if r.Error != "" {
		fmt.Fprintf(w, "\t%s\n", r.Error)
	}
	if r.Response != nil {
		data, _ := json.MarshalIndent(r.Response, "", "  ")
		fmt.Fprintf(w, "response:\n%s\n", string(data))
	}
	if c := r.Census; c != nil {
		fmt.Fprintf(w, "census: tick=%.0f paused=%t authority=%t ownedDrafts=%d loadToken=%s\n", c.Tick, c.Paused, c.AuthorityActive, c.OwnedDrafts, c.LoadToken)
		names := make([]string, 0, len(c.Stock))
		for name, counts := range c.Stock {
			if counts.Units != 0 {
				names = append(names, name)
			}
		}
		sort.Strings(names)
		for _, name := range names {
			counts := c.Stock[name]
			if counts.Forbidden >= 0 {
				fmt.Fprintf(w, "  %s\t%d\t(forbidden %d)\n", name, counts.Units, counts.Forbidden)
			} else {
				fmt.Fprintf(w, "  %s\t%d\n", name, counts.Units)
			}
		}
	}
}
