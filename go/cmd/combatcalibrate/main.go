// Command combatcalibrate runs the sim-vs-real calibration gate for the
// combatsim ranged slice (#2844). Run it from go/:
//
//	go run ./cmd/combatcalibrate [-record]
//
// It plays every calibration and held-out lab fixture in the simulator under
// each tactic variant over the baseline's seeds, pairs the result with the
// committed real-game sweeps (the baselines, and the variant sweeps under
// combatlab/calibration/variants), prints the report and exits 1 when the gate
// fails. -record also writes report.json and report.txt into the calibration
// directory; a failing report is a valid record.
package main

import (
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim/calibrate"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/combatlab"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

func main() { os.Exit(run(os.Args[1:], os.Stdout, os.Stderr)) }

func run(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("combatcalibrate", flag.ContinueOnError)
	fs.SetOutput(stderr)
	record := fs.Bool("record", false, "write the report into the calibration directory")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	dir, err := filepath.Abs(combatlab.CalibrationDir)
	if err != nil {
		fmt.Fprintln(stderr, "combatcalibrate:", err)
		return 2
	}
	// The recorded catalog is found by walking up from the working directory
	// to internal/observation/testdata, so read it from go/internal.
	if err := os.Chdir("internal"); err != nil {
		fmt.Fprintln(stderr, "combatcalibrate: run from go/:", err)
		return 2
	}
	cat, err := recordedcatalog.Decode()
	if err != nil {
		fmt.Fprintln(stderr, "combatcalibrate:", err)
		return 2
	}
	in, err := combatlab.CalibrationInputs(cat, dir)
	if err != nil {
		fmt.Fprintln(stderr, "combatcalibrate:", err)
		return 2
	}
	rep := calibrate.Evaluate(calibrate.CurrentStamp(), in)
	fmt.Fprint(stdout, rep.Text())
	if *record {
		data, err := rep.Marshal()
		if err == nil {
			err = os.WriteFile(filepath.Join(dir, "report.json"), append(data, '\n'), 0o644)
		}
		if err == nil {
			err = os.WriteFile(filepath.Join(dir, "report.txt"), []byte(rep.Text()), 0o644)
		}
		if err != nil {
			fmt.Fprintln(stderr, "combatcalibrate:", err)
			return 2
		}
	}
	return rep.ExitCode()
}
