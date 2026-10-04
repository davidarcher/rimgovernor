// Command checktesttimes is a hang guard over the
// output of `go test -json`. It fails the build when any single test
// runs past the hang bound, so a structurally hung test fails CI.
// It is not a latency gate: the bound is far above any healthy run.
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
	max := flag.Duration("hang", 60*time.Second, "hang guard: a single test running longer than this is treated as hung (not a latency gate)")
	budget := flag.Duration("budget", 0, "per-test budget: fail when any test not marked slowtest.Skip runs longer than this (0 = off); the nightly uses 1s under -short")
	skipped := flag.String("skipped", "", "write a markdown report of every test skipped with a \"slow:\" reason to this file")
	flag.Parse()
	os.Exit(runOpts(os.Stdin, os.Stderr, options{hang: *max, budget: *budget, skippedPath: *skipped}))
}

type options struct {
	hang        time.Duration
	budget      time.Duration
	skippedPath string
}

func run(r io.Reader, w io.Writer, max time.Duration) int {
	return runOpts(r, w, options{hang: max})
}

// slowSkipPrefix is the message slowtest.Skip prepends to its reason.
const slowSkipPrefix = "slow: "

func runOpts(r io.Reader, w io.Writer, opt options) int {
	max := opt.hang
	type slow struct {
		pkg, name string
		elapsed   time.Duration
	}
	var slowTests []slow
	var skippedSlow []skippedTest
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
			limit := max
			if opt.budget > 0 && opt.budget < limit {
				limit = opt.budget
			}
			if elapsed > limit {
				slowTests = append(slowTests, slow{pkg: e.Package, name: e.Package + "." + e.Test, elapsed: elapsed})
			}
		case "skip":
			if e.Test == "" {
				continue
			}
			for _, line := range output[k] {
				if i := strings.Index(line, slowSkipPrefix); i >= 0 && strings.Contains(line[:i], ".go:") {
					skippedSlow = append(skippedSlow, skippedTest{name: e.Package + "." + e.Test, reason: strings.TrimSpace(line[i+len(slowSkipPrefix):])})
					break
				}
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
		if opt.budget > 0 && opt.budget < max {
			fmt.Fprintf(w, "checktesttimes: %d unmarked test(s) ran past the %s budget:\n", len(slowTests), opt.budget)
			for _, s := range slowTests {
				fmt.Fprintf(w, "  %s took %s\n", s.name, s.elapsed)
			}
			fmt.Fprintln(w, "Make the test faster, or mark it with slowtest.Skip(t, reason) so -short skips it.")
		} else {
			fmt.Fprintf(w, "checktesttimes: %d test(s) ran past the %s hang bound:\n", len(slowTests), max)
			for _, s := range slowTests {
				fmt.Fprintf(w, "  %s took %s\n", s.name, s.elapsed)
			}
			fmt.Fprintln(w, "A test this long is hung or structurally broken, not merely slow.")
			fmt.Fprintln(w, "Fix the test (find the hang) rather than raising the bound.")
		}
	}

	if opt.skippedPath != "" {
		if err := os.WriteFile(opt.skippedPath, []byte(skippedReport(skippedSlow)), 0o644); err != nil {
			fmt.Fprintln(w, "checktesttimes: writing skipped report:", err)
			return 1
		}
	}

	if failed || len(slowTests) > 0 {
		return 1
	}
	fmt.Fprintf(w, "checktesttimes: %d tests passed under the %s hang bound; slowest %s took %s\n", passed, max, slowest.name, slowest.elapsed.Round(time.Millisecond))
	return 0
}

type skippedTest struct{ name, reason string }

// skippedReport lists every test slowtest.Skip skipped, as markdown suited to
// a GitHub step summary, so a marker cannot skip a test forever unseen.
func skippedReport(tests []skippedTest) string {
	sort.Slice(tests, func(i, j int) bool { return tests[i].name < tests[j].name })
	var b strings.Builder
	fmt.Fprintf(&b, "### Tests skipped under -short by slowtest.Skip: %d\n\n", len(tests))
	if len(tests) > 0 {
		b.WriteString("| Test | Reason |\n| --- | --- |\n")
	}
	for _, t := range tests {
		fmt.Fprintf(&b, "| `%s` | %s |\n", t.name, strings.ReplaceAll(t.reason, "|", `\|`))
	}
	return b.String()
}
