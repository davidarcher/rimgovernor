// Command rimgovernor exposes the gated Go migration tools.
package main

import (
	"fmt"
	"io"
	"os"
)

func run(args []string, out, errors io.Writer) int {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "help" || args[0] == "--help")) {
		fmt.Fprintln(out, "RimGovernor Go migration tools\nUsage: rimgovernor version\nNative runtime is unavailable in this build.")
		return 0
	}
	if len(args) == 1 && args[0] == "version" {
		fmt.Fprintln(out, "RimGovernor Go foundation (G01; native runtime unavailable)")
		return 0
	}
	fmt.Fprintln(errors, "unsupported command; run rimgovernor help")
	return 2
}

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}
