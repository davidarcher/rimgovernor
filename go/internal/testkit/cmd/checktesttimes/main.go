// Command checktesttimes enforces a per-test wall-clock budget over the
// output of `go test -json`. It fails the build when any single test
// exceeds the budget, so a slow test regresses CI instead of silently
// growing the suite's wall time.
//
// It also trims the log: `go test -json` captures full verbose output
// (every RUN/PASS line) regardless of -v, which is too noisy to be useful
// in CI. This only prints the buffered output for a test (or package) that
// actually failed; passing tests are summarized, not echoed line by line.
package main

import (
	"bufio"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"sort"
	"strings"
	"time"
)

type event struct {
	Action  string
	Package string
	Test    string
	Elapsed float64
	Output  string
}

type testKey struct{ pkg, test string }

func main() {
	max := flag.Duration("max", 10*time.Second, "maximum wall-clock time allowed for a single test")
	flag.Parse()
	os.Exit(run(os.Stdin, os.Stderr, *max))
}

func run(r io.Reader, w io.Writer, max time.Duration) int {
	type slow struct {
		pkg, name string
		elapsed   time.Duration
	}
	var slowTests []slow
	var keyOrder []testKey
	passed := 0
	slowestBy := map[string]slow{} // per package; the headline picks among uncached ones
	output := map[testKey][]string{}
	failedKey := map[testKey]bool{}
	failedPackage := map[string]bool{}
	// cached packages replay a previous run's timings, so a budget breach in
	// one says nothing about this run: go test prints "(cached)" on the
	// package's ok line and reports the old Elapsed for every test (#334).
	cached := map[string]bool{}

	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		line := scanner.Bytes()
		var e event
		if err := json.Unmarshal(line, &e); err != nil {
			// Not every line of `go test -json` is a JSON test event (e.g. a
			// bare `go: downloading ...` line on a cold module cache);
			// pass it through unmodified rather than failing the build on it.
			fmt.Fprintln(w, string(line))
			continue
		}
		k := testKey{pkg: e.Package, test: e.Test}
		switch e.Action {
		case "output":
			if len(output[k]) == 0 {
				keyOrder = append(keyOrder, k)
			}
			output[k] = append(output[k], e.Output)
			if e.Test == "" && strings.Contains(e.Output, "(cached)") {
				cached[e.Package] = true
			}
		case "fail":
			failedKey[k] = true
			if e.Test == "" {
				failedPackage[e.Package] = true
			}
		case "pass":
			if e.Test == "" {
				continue
			}
			elapsed := time.Duration(e.Elapsed * float64(time.Second))
			passed++
			if elapsed > slowestBy[e.Package].elapsed {
				slowestBy[e.Package] = slow{pkg: e.Package, name: e.Package + "." + e.Test, elapsed: elapsed}
			}
			if elapsed > max {
				slowTests = append(slowTests, slow{pkg: e.Package, name: e.Package + "." + e.Test, elapsed: elapsed})
			}
		}
	}
	if err := scanner.Err(); err != nil {
		fmt.Fprintln(w, "checktesttimes: reading test output:", err)
		return 1
	}

	live := slowTests[:0]
	for _, s := range slowTests {
		if !cached[s.pkg] {
			live = append(live, s)
		}
	}
	slowTests = live
	slowest := slow{}
	for pkg, s := range slowestBy {
		if !cached[pkg] && s.elapsed > slowest.elapsed {
			slowest = s
		}
	}

	var failedNames []string
	for _, k := range keyOrder {
		if failedKey[k] || failedPackage[k.pkg] {
			for _, line := range output[k] {
				fmt.Fprint(w, line)
			}
		}
	}
	for k := range failedKey {
		if k.test != "" {
			failedNames = append(failedNames, k.pkg+"."+k.test)
		}
	}
	for pkg := range failedPackage {
		failedNames = append(failedNames, pkg)
	}
	sort.Strings(failedNames)
	for _, name := range failedNames {
		fmt.Fprintf(w, "FAIL %s\n", name)
	}
	failed := len(failedNames) > 0

	if len(slowTests) > 0 {
		sort.Slice(slowTests, func(i, j int) bool { return slowTests[i].elapsed > slowTests[j].elapsed })
		fmt.Fprintf(w, "checktesttimes: %d test(s) exceeded the %s budget:\n", len(slowTests), max)
		for _, s := range slowTests {
			fmt.Fprintf(w, "  %s took %s\n", s.name, s.elapsed)
		}
		fmt.Fprintln(w, "A slow test usually means real transactions/IO/sleeps in a loop, not a slow environment.")
		fmt.Fprintln(w, "Fix the test (shrink scale, remove real sleeps, batch IO) rather than raising the budget.")
	}

	if failed || len(slowTests) > 0 {
		return 1
	}
	fmt.Fprintf(w, "checktesttimes: %d tests passed within the %s budget; slowest %s took %s\n", passed, max, slowest.name, slowest.elapsed.Round(time.Millisecond))
	return 0
}
