package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

const setupUsage = `  acceptance setup [-worktree <dir>] [-rimworld <RimWorld dir>] [-harmony <0Harmony.dll>] [-gabs <gabs.exe>]
                   [-fixture A,B | -production] [-rebuild] [-skip-mod] [-skip-binaries]
`

// setupOptions are the parsed setup flags.
type setupOptions struct {
	repo      string
	layoutDir string
	overrides setup.Overrides
	run       setup.Options
}

// parseSetup resolves the setup flags: the worktree (the checkout
// enclosing the working directory unless -worktree names one) and the
// discovery overrides. -fixture narrows the mod build to the named
// classes (every class the build script accepts by default); -production
// builds no fixtures at all (the build storage/food cases need).
func parseSetup(args []string, stderr io.Writer) (setupOptions, error) {
	fs := flag.NewFlagSet("setup", flag.ContinueOnError)
	fs.SetOutput(stderr)
	var o setupOptions
	var fixtures string
	fs.StringVar(&o.repo, "worktree", "", "checkout to set up (default: the one enclosing the working directory)")
	fs.StringVar(&o.layoutDir, "layout", "", "private writable layout directory (default: <worktree>/.rimgovernor)")
	fs.StringVar(&o.overrides.BridgeDir, "bridge", "", "explicit RimBridgeServer runtime directory")
	fs.StringVar(&o.overrides.HarmonyMod, "harmony-mod", "", "explicit complete Harmony runtime mod directory")
	fs.StringVar(&o.overrides.RimBridgeSDK, "sdk", "", "explicit RimBridgeServer SDK assemblies directory")
	fs.BoolVar(&o.overrides.Explicit, "explicit", false, "require every dependency path and create a clean profile without machine discovery")
	fs.StringVar(&o.overrides.RimWorldDir, "rimworld", "", "RimWorld install holding RimWorldWin64.exe (default: Steam, or $"+setup.RimWorldDirEnv+")")
	fs.StringVar(&o.overrides.Harmony, "harmony", "", "0Harmony.dll (default: the Steam workshop item, or $"+setup.HarmonyEnv+")")
	fs.StringVar(&o.overrides.GABS, "gabs", "", "gabs.exe to install (default: a peer worktree's, or $"+setup.GABSEnv+")")
	fs.StringVar(&fixtures, "fixture", "", "comma-separated fixture classes for the mod build (default: all)")
	fs.BoolVar(&o.run.Production, "production", false, "build the mod without fixtures")
	fs.BoolVar(&o.run.Rebuild, "rebuild", false, "rebuild and reinstall the mod even when the installed build is current")
	fs.BoolVar(&o.run.SkipMod, "skip-mod", false, "leave the installed mod alone")
	fs.BoolVar(&o.run.SkipBinaries, "skip-binaries", false, "leave .rimgovernor/bin alone")
	if err := fs.Parse(args); err != nil {
		return o, err
	}
	if len(fs.Args()) > 0 {
		return o, fmt.Errorf("setup takes no positional arguments: %v", fs.Args())
	}
	if fixtures != "" && o.run.Production {
		return o, fmt.Errorf("-fixture and -production are exclusive")
	}
	if fixtures != "" {
		for _, f := range strings.Split(fixtures, ",") {
			if f = strings.TrimSpace(f); f != "" {
				o.run.Fixtures = append(o.run.Fixtures, f)
			}
		}
	}
	if o.repo == "" {
		cwd, err := os.Getwd()
		if err != nil {
			return o, err
		}
		repo, ok := na.FindRepo(cwd)
		if !ok {
			return o, fmt.Errorf("no checkout encloses %s; pass -worktree", cwd)
		}
		o.repo = repo
	} else if _, err := os.Stat(filepath.Join(o.repo, ".git")); err != nil {
		return o, fmt.Errorf("-worktree %s is not a checkout (no .git): %v", o.repo, err)
	}
	o.overrides.Repo = o.repo
	return o, nil
}

// runSetup discovers the inputs and runs the setup, printing the summary
// and the run command a first case needs.
func runSetup(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	o, err := parseSetup(args, stderr)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	inputs, err := setup.Discover(o.overrides)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	o.run.Layout = setup.NewLayout(o.repo)
	if o.layoutDir != "" {
		if !filepath.IsAbs(o.layoutDir) {
			fmt.Fprintln(stderr, "-layout must be absolute")
			return 2
		}
		o.run.Layout.GameCopy = filepath.Join(o.layoutDir, "native-rimworld")
		o.run.Layout.Root = filepath.Join(o.layoutDir, "bridge")
		o.run.Layout.Builds = filepath.Join(o.layoutDir, "native-builds")
		o.run.Layout.Bin = filepath.Join(o.layoutDir, "bin")
	}
	o.run.Inputs = inputs
	o.run.Log = stdout
	s, err := setup.Run(ctx, o.run)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	fixtures, mod := fixtureSummary(s.Fixtures), s.ModReason
	if o.run.SkipMod {
		fixtures, mod = "unchanged", "left alone (-skip-mod)"
	}
	fmt.Fprintf(stdout, "\nready\n  root      %s\n  game copy %s\n  fixtures  %s\n  mod       %s\n", s.Layout.Root, s.Layout.GameCopy, fixtures, mod)
	fmt.Fprintf(stdout, "\nrun a case:\n  %s\nstop the kept game:\n  %s\n", s.RunCommand, s.StopCommand)
	return 0
}

func fixtureSummary(fixtures []string) string {
	if len(fixtures) == 0 {
		return "none (production build)"
	}
	return fmt.Sprintf("%d classes", len(fixtures))
}
