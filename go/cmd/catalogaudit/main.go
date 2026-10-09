// Command catalogaudit reports how much of the game the def mirror covers:
//
//	go run ./cmd/catalogaudit [-rimworld <dir>] [-recording <path>]
//
// It prints four sections:
//
//  1. Def coverage: per def class, the rows in the recorded catalog against
//     the distinct <Def> elements with a defName under Data/*/Defs/**.xml
//     (abstract defs excluded, tag case ignored). A class with more XML defs
//     than rows, or with no message at all, is flagged; a duplicate defName
//     is reported as such, not as a gap.
//  2. Skipped fields: the runtime-state fields the generator leaves out of
//     contracts/proto/defs.proto, counted by reason from its header.
//  3. Constants: the game's const and static readonly scalars and enums and
//     its static SimpleCurves in the gameplay namespaces, against the fields
//     the catalog carries (CatalogConstants).
//  4. Unsaved data fields: [Unsaved] fields that are data, not runtime state,
//     per Def class.
//
// Sections 3 and 4 reflect the game assemblies through tools/defmirror
// (--report), which needs the dotnet SDK. The exit status is 1 when a class
// has XML defs but no rows.
package main

import (
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

func main() {
	rimworld := flag.String("rimworld", "", "RimWorld install directory (default: discovered, or $"+setup.RimWorldDirEnv+")")
	recording := flag.String("recording", filepath.FromSlash("internal/observation/testdata/full_catalog.pb.gz"), "recorded definition catalog")
	defsProto := flag.String("defs", filepath.FromSlash("../contracts/proto/defs.proto"), "generated defs.proto whose header lists the skipped fields")
	defmirror := flag.String("defmirror", filepath.FromSlash("../tools/defmirror"), "tools/defmirror project directory")
	flag.Parse()
	failed, err := run(os.Stdout, *rimworld, *recording, *defsProto, *defmirror)
	if err != nil {
		fmt.Fprintln(os.Stderr, "catalogaudit:", err)
		os.Exit(2)
	}
	if failed {
		os.Exit(1)
	}
}

func run(out io.Writer, rimworld, recording, defsProto, defmirror string) (bool, error) {
	in, err := setup.Discover(setup.Overrides{RimWorldDir: rimworld})
	if err != nil {
		return false, err
	}
	rows, messages, err := recordedRows(recording)
	if err != nil {
		return false, err
	}
	xmlDefs, err := loadXMLDefs(filepath.Join(in.RimWorldDir, "Data"))
	if err != nil {
		return false, err
	}
	cov := Coverage(xmlDefs, rows, messages)
	fmt.Fprint(out, FormatCoverage(cov))

	header, err := os.ReadFile(defsProto)
	if err != nil {
		return false, err
	}
	fmt.Fprint(out, FormatSkipped(SkippedByReason(string(header))))

	report, err := reflectGame(defmirror, in.ManagedDir)
	if err != nil {
		return false, err
	}
	members := ParseReport(report)
	fmt.Fprint(out, FormatConstants(members, catalogConstantFields()))
	fmt.Fprint(out, FormatUnsaved(members))
	return cov.Failed(), nil
}

// reflectGame runs tools/defmirror --report against the game's Managed
// directory and returns the report text.
func reflectGame(defmirror, managed string) (string, error) {
	tmp, err := os.CreateTemp("", "catalogaudit-*.tsv")
	if err != nil {
		return "", err
	}
	tmp.Close()
	defer os.Remove(tmp.Name())
	project := filepath.Join(defmirror, "DefMirror.csproj")
	for _, args := range [][]string{
		{"restore", project, "--locked-mode"},
		{"run", "--project", defmirror, "-c", "Release", "--no-restore", "--no-launch-profile", "--",
			"--managed-dir", managed, "--report", tmp.Name()},
	} {
		cmd := exec.Command("dotnet", args...)
		cmd.Stdout = io.Discard
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return "", fmt.Errorf("dotnet %s: %w", args[0], err)
		}
	}
	data, err := os.ReadFile(tmp.Name())
	return string(data), err
}
