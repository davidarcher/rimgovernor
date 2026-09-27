//go:build !windows

package main

import (
	"fmt"
	"os"
)

func main() {
	fmt.Fprintln(os.Stderr, "the RimGovernor launcher runs on Windows only")
	os.Exit(1)
}
