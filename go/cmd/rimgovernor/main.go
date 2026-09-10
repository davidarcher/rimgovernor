// Command rimgovernor exposes the gated Go migration tools.
package main

import (
	"fmt"
	"io"
	"os"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

func run(args []string, out, errors io.Writer) int {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "help" || args[0] == "--help")) {
		fmt.Fprintln(out, "RimGovernor Go migration tools\nUsage: rimgovernor version\n       rimgovernor replay <expected.json> <actual.json>\nNative runtime is unavailable in this build.")
		return 0
	}
	if len(args) == 1 && args[0] == "version" {
		fmt.Fprintln(out, "RimGovernor Go foundation (G01; native runtime unavailable)")
		return 0
	}
	if args[0] == "replay" {
		if len(args) != 3 {
			fmt.Fprintln(errors, "usage: rimgovernor replay <expected.json> <actual.json>")
			return 2
		}
		return replayFiles(args[1], args[2], out, errors)
	}
	fmt.Fprintln(errors, "unsupported command; run rimgovernor help")
	return 2
}

func replayFiles(expectedPath, actualPath string, out, errors io.Writer) int {
	expected, err := os.Open(expectedPath)
	if err != nil {
		fmt.Fprintf(errors, "replay expected: %v\n", err)
		return 1
	}
	defer expected.Close()
	actual, err := os.Open(actualPath)
	if err != nil {
		fmt.Fprintf(errors, "replay actual: %v\n", err)
		return 1
	}
	defer actual.Close()
	comparison, err := testkit.CompareJSON(expected, actual)
	if err != nil {
		fmt.Fprintf(errors, "replay: %v\n", err)
		return 1
	}
	if !comparison.Equal {
		fmt.Fprintf(errors, "replay differs at compacted byte %d\n", comparison.FirstDifference)
		return 1
	}
	fmt.Fprintln(out, "replay matches")
	return 0
}

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}
