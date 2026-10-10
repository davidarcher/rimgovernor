// Command stataudit regenerates, or checks, the table of every concrete
// StatWorker and StatPart class of the installed game: the StatDefs that use
// it, a category, the inputs its decompiled body reads, a hash of that body
// and the Go owner or "unowned".
//
//	go run ./cmd/stataudit [-rimworld <dir>] [-out <table>] [-decompiled <dir>]
//	go run ./cmd/stataudit --check [-rimworld <dir>] [-decompiled <dir>]
//
// It needs ilspycmd, a global dotnet tool that nothing in the repository
// pins:
//
//	dotnet tool install -g ilspycmd
//
// Regeneration writes -out (default cmd/stataudit/stat_classes.tsv relative
// to go/) and keeps the hand-maintained owner of every class that remains.
// --check does not write: it fails when a class is new, gone, its hash
// differs from the checked-in (embedded) table (the game's code changed since
// the table was written), or the table lists a class as unowned.
package main

import (
	_ "embed"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

//go:embed stat_classes.tsv
var embeddedTable string

func main() {
	rimworld := flag.String("rimworld", "", "RimWorld install directory (default: discovered, or $"+setup.RimWorldDirEnv+")")
	out := flag.String("out", filepath.FromSlash("cmd/stataudit/stat_classes.tsv"), "table to write")
	decompiled := flag.String("decompiled", "", "directory for the decompiled sources (default: temporary, removed afterwards)")
	check := flag.Bool("check", false, "fail when a class is new or its decompiled body changed since the table was written")
	flag.Parse()
	if err := run(*rimworld, *out, *decompiled, *check); err != nil {
		fmt.Fprintln(os.Stderr, "stataudit:", err)
		os.Exit(1)
	}
}

func run(rimworld, out, decompiled string, check bool) error {
	in, err := setup.Discover(setup.Overrides{RimWorldDir: rimworld})
	if err != nil {
		return err
	}
	if decompiled == "" {
		tmp, err := os.MkdirTemp("", "stataudit-")
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
	classes, err := scanClasses(decompiled)
	if err != nil {
		return err
	}
	use, err := loadStatUses(filepath.Join(in.RimWorldDir, "Data"))
	if err != nil {
		return err
	}
	rows := BuildRows(classes, use)
	if check {
		table, err := Parse(embeddedTable)
		if err != nil {
			return err
		}
		if problems := Check(table, rows); len(problems) > 0 {
			for _, p := range problems {
				fmt.Fprintln(os.Stderr, p)
			}
			return fmt.Errorf("%d class(es) differ from the table; rerun without --check, then review the owner of each", len(problems))
		}
		fmt.Printf("stataudit: %d classes match the table\n", len(rows))
		return nil
	}
	if old, err := os.ReadFile(out); err == nil {
		oldRows, err := Parse(string(old))
		if err != nil {
			return err
		}
		KeepOwners(rows, oldRows)
	}
	if err := os.WriteFile(out, []byte(Format(rows)), 0o644); err != nil {
		return err
	}
	fmt.Printf("stataudit: wrote %d rows to %s\n", len(rows), out)
	return nil
}
