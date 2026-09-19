package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/doctor"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

// preflight is run's first step: the doctor's checks, only the failing
// ones printed. A Fail refuses the run before any game opens, except a
// stale or fixture-less install (doctor.Check.Code), which the run heals
// (heal) unless opts.NoHeal; the checks then run again. It returns what
// was healed (Options.Healed) and whether the run may go on.
func preflight(ctx context.Context, selected []cases.Case, opts cases.Options, stdout io.Writer) ([]string, bool) {
	names := make([]string, 0, len(selected))
	var ops []string
	for _, c := range selected {
		if c.Name != "" {
			names = append(names, c.Name)
		}
		ops = append(ops, c.FixtureOps()...)
	}
	o := doctor.Options{Root: opts.Root, Rimgovernor: opts.Rimgovernor, Output: opts.Output, Cases: names, GameID: opts.GameID, FixtureOps: ops}
	checks := doctor.Run(ctx, o)
	doctor.Write(stdout, checks, true)
	var healed []string
	if !opts.NoHeal {
		if n := stopOrphans(ctx, checks, stdout); n > 0 {
			healed = append(healed, doctor.HealOrphans)
		}
	}
	if !doctor.Failed(checks) {
		return healed, true
	}
	codes := healable(checks)
	if len(codes) == 0 || opts.NoHeal {
		if len(codes) > 0 {
			fmt.Fprintln(stdout, "preflight failed; -no-heal refuses the rebuild a run would otherwise do (acceptance setup fixes it)")
		} else {
			fmt.Fprintln(stdout, "preflight failed; fix the checks above or pass -no-doctor to run anyway")
		}
		return nil, false
	}
	rebuilt, err := heal(ctx, codes, ops, opts, stdout)
	healed = append(healed, rebuilt...)
	if err != nil {
		fmt.Fprintf(stdout, "heal failed: %v\npreflight failed; fix the checks above (acceptance setup) or pass -no-doctor to run anyway\n", err)
		return nil, false
	}
	checks = doctor.Run(ctx, o)
	doctor.Write(stdout, checks, true)
	if doctor.Failed(checks) {
		fmt.Fprintln(stdout, "preflight still fails after the heal; fix the checks above or pass -no-doctor to run anyway")
		return healed, false
	}
	return healed, true
}

// stopOrphans stops the harness processes the orphans check found
// running from removed worktrees (#346): nobody's game, so a preflight
// ends them without asking. It returns how many it stopped.
func stopOrphans(ctx context.Context, checks []doctor.Check, log io.Writer) int {
	for _, c := range checks {
		if c.Code != doctor.HealOrphans || len(c.PIDs) == 0 {
			continue
		}
		stopCtx, cancel := context.WithTimeout(ctx, time.Minute)
		defer cancel()
		if err := setup.StopPIDs(stopCtx, c.PIDs); err != nil {
			fmt.Fprintf(log, "heal\torphans\t%v\n", err)
			return 0
		}
		fmt.Fprintf(log, "heal\torphans\tstopped pids %v from removed worktrees\n", c.PIDs)
		return len(c.PIDs)
	}
	return 0
}

// healable lists the heal codes of the failing checks, or nothing when
// any Fail carries no code: a heal fixes the install, never the rest.
func healable(checks []doctor.Check) []string {
	var codes []string
	for _, c := range checks {
		if c.Status != doctor.Fail {
			continue
		}
		if c.Code == "" {
			return nil
		}
		codes = append(codes, c.Code)
	}
	return codes
}

// heal rebuilds and reinstalls the mod a preflight found stale or short
// of a fixture (#276), the way the by-hand recipe went: the fixture set
// is the installed build's plus the classes registering ops (the run's
// cases' fixture ops, na.FixtureFlags); the root's own kept game is
// stopped first, through its GABS configuration and then by pid under
// the worktree's private game copy (setup.StopGames), never by image
// name; setup.Run then builds and installs into that copy (the binaries
// are left alone: this one is running). It only heals a root that is the
// worktree's own layout (setup.NewLayout): any other root is refused, as
// the install would land in a copy the run does not launch. The next
// OpenGame launches fresh. It returns codes plus "relaunched" when a game
// was stopped, for the report's healed list.
func heal(ctx context.Context, codes, ops []string, opts cases.Options, log io.Writer) ([]string, error) {
	cwd, err := os.Getwd()
	if err != nil {
		return nil, err
	}
	repo, ok := na.FindRepo(cwd)
	if !ok {
		return nil, fmt.Errorf("no checkout encloses %s; run from the worktree", cwd)
	}
	layout := setup.NewLayout(repo)
	gameCopy, err := rootGameCopy(opts)
	if err != nil {
		return nil, err
	}
	if !strings.EqualFold(filepath.Clean(gameCopy), filepath.Clean(layout.GameCopy)) {
		return nil, fmt.Errorf("%s launches %s, not this worktree's private copy %s; only the worktree's own layout is healed", opts.Root, gameCopy, layout.GameCopy)
	}
	installed := filepath.Join(gameCopy, "Mods", "RimGovernor")
	var installedFixtures []string
	production := false
	if manifest, err := na.ReadPackageManifest(installed); err == nil {
		installedFixtures, production = manifest.Fixtures, manifest.Role == "production"
	}
	fixtures := na.FixtureFlags(repo, installedFixtures, ops)
	// A production install stays production unless the run needs a
	// fixture; a fixture install with nothing recorded takes every class.
	production = production && len(fixtures) == 0
	fmt.Fprintf(log, "heal\t%s\trebuilding the mod (fixtures %v, production %v)\n", strings.Join(codes, ","), fixtures, production)

	healed := append([]string{}, codes...)
	stopCtx, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	running, err := setup.RunningGames(gameCopy)
	if err != nil {
		return nil, err
	}
	if len(running) > 0 {
		// The root's GABS owns the kept process: games_stop is the clean
		// end. Whatever it does not know about (a stale runtime) is still
		// this worktree's game by its executable path, and goes by pid.
		if err := na.StopGame(stopCtx, opts.Root, opts.GameID); err != nil {
			fmt.Fprintf(log, "heal\tgames_stop\t%v (falling back to the copy's pids)\n", err)
		}
		if _, err := setup.StopGames(stopCtx, gameCopy); err != nil {
			return nil, fmt.Errorf("stop the root's game: %w", err)
		}
		fmt.Fprintf(log, "heal\tstopped\tpids %v from %s; the run launches fresh\n", running, gameCopy)
		healed = append(healed, "relaunched")
	}

	inputs, err := setup.Discover(setup.Overrides{Repo: repo})
	if err != nil {
		return nil, err
	}
	s, err := setup.Run(ctx, setup.Options{Layout: layout, Inputs: inputs, Fixtures: fixtures, Production: production, SkipBinaries: true, Log: log})
	if err != nil {
		return nil, err
	}
	if !s.ModBuilt {
		return nil, errors.New("setup left the installed build alone (" + s.ModReason + ") though the preflight found it wanting")
	}
	fmt.Fprintf(log, "heal\tinstalled\t%s (%s)\n", s.ModBuild, s.ModReason)
	return healed, nil
}

// rootGameCopy is the game copy the root's config launches.
func rootGameCopy(opts cases.Options) (string, error) {
	cfg := &na.Config{Root: opts.Root, GameID: opts.GameID, Configuration: filepath.Join(opts.Root, "config")}
	game, err := cfg.GameSection()
	if err != nil {
		return "", err
	}
	workingDir, _ := game["workingDir"].(string)
	if workingDir == "" {
		return "", fmt.Errorf("%s: the game has no workingDir", filepath.Join(opts.Root, "config", "config.json"))
	}
	return workingDir, nil
}
