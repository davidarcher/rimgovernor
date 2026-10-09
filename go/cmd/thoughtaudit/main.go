// Command thoughtaudit regenerates the thought-to-trigger table: one row
// per vanilla ThoughtDef with the game state it depends on and how the game
// grants it, read from the installed game's code.
//
//	go run ./cmd/thoughtaudit [-rimworld <dir>] [-out <table>] [-decompiled <dir>]
//
// It needs ilspycmd, a global dotnet tool that nothing in the repository
// pins:
//
//	dotnet tool install -g ilspycmd
//
// The tool decompiles RimWorldWin64_Data/Managed/Assembly-CSharp.dll
// (into -decompiled, default a temporary directory; an existing non-empty
// -decompiled is reused), reads every ThoughtDef under Data/*/Defs, and
// writes -out (default internal/policy/thought_triggers.tsv relative to
// go/). Situational thoughts (a workerClass) record what the worker reads;
// memory thoughts record the code and XML sites that grant them. The
// owner column is hand-maintained: regeneration keeps the owner of every
// def that remains and leaves new defs unowned. There is no check mode;
// a stale row simply shows up as unowned.
package main

import (
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

func main() {
	rimworld := flag.String("rimworld", "", "RimWorld install directory (default: discovered, or $"+setup.RimWorldDirEnv+")")
	out := flag.String("out", filepath.FromSlash("internal/policy/thought_triggers.tsv"), "table to write")
	decompiled := flag.String("decompiled", "", "directory for the decompiled sources (default: temporary, removed afterwards)")
	flag.Parse()
	if err := run(*rimworld, *out, *decompiled); err != nil {
		fmt.Fprintln(os.Stderr, "thoughtaudit:", err)
		os.Exit(1)
	}
}

func run(rimworld, out, decompiled string) error {
	in, err := setup.Discover(setup.Overrides{RimWorldDir: rimworld})
	if err != nil {
		return err
	}
	if decompiled == "" {
		tmp, err := os.MkdirTemp("", "thoughtaudit-")
		if err != nil {
			return err
		}
		defer os.RemoveAll(tmp)
		decompiled = tmp
	}
	if entries, _ := os.ReadDir(decompiled); len(entries) == 0 {
		if _, err := exec.LookPath("ilspycmd"); err != nil {
			return fmt.Errorf("ilspycmd not found; install it with: dotnet tool install -g ilspycmd")
		}
		cmd := exec.Command("ilspycmd", "-p", "-o", decompiled, filepath.Join(in.ManagedDir, "Assembly-CSharp.dll"))
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("ilspycmd: %w", err)
		}
	}
	defs, xmlFiles, err := loadThoughtDefs(filepath.Join(in.RimWorldDir, "Data"))
	if err != nil {
		return err
	}
	names := map[string]bool{}
	for _, d := range defs {
		names[d.Name] = true
	}
	workers, sites, err := scanSources(decompiled, names)
	if err != nil {
		return err
	}
	rows := BuildRows(defs, workers, sites, xmlFiles)
	if old, err := os.ReadFile(out); err == nil {
		owners := ParseOwners(string(old))
		for i := range rows {
			rows[i].Owner = owners[rows[i].Def]
		}
	}
	if err := os.WriteFile(out, []byte(Format(rows)), 0o644); err != nil {
		return err
	}
	fmt.Printf("thoughtaudit: wrote %d rows to %s\n", len(rows), out)
	return nil
}
