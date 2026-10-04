package affected

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"sort"
	"time"
)

// slowBudget is the duration past which a test that ran under -short should
// carry the slowtest.Skip marker (#2011). It is a warning only: under load
// durations inflate, so cmd/test never fails on it; the nightly enforces it.
const slowBudget = time.Second

type testEvent struct {
	Action  string
	Package string
	Test    string
	Elapsed float64
	Output  string
}

type slowTest struct {
	name    string
	elapsed time.Duration
}

// renderTestJSON reads `go test -json` from r, writes the text go test would
// have printed to w (package lines, plus a failed test's own output) and
// returns the tests that ran (not skipped, so not marked slow:) longer than
// slowBudget, slowest first. Lines that are not JSON pass through.
func renderTestJSON(r io.Reader, w io.Writer) []slowTest {
	var slow []slowTest
	buffered := map[[2]string][]string{}
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		var e testEvent
		if err := json.Unmarshal(scanner.Bytes(), &e); err != nil {
			fmt.Fprintln(w, scanner.Text())
			continue
		}
		key := [2]string{e.Package, e.Test}
		switch e.Action {
		case "output":
			if e.Test == "" {
				if e.Output == "PASS\n" { // -json emits it; plain go test does not
					continue
				}
				fmt.Fprint(w, e.Output)
			} else {
				buffered[key] = append(buffered[key], e.Output)
			}
		case "fail", "pass":
			if e.Action == "fail" {
				for _, line := range buffered[key] {
					fmt.Fprint(w, line)
				}
			}
			if elapsed := time.Duration(e.Elapsed * float64(time.Second)); e.Test != "" && elapsed > slowBudget {
				slow = append(slow, slowTest{e.Package + "." + e.Test, elapsed})
			}
			delete(buffered, key)
		case "skip":
			delete(buffered, key)
		}
	}
	sort.Slice(slow, func(i, j int) bool { return slow[i].elapsed > slow[j].elapsed })
	return slow
}

// warnSlow prints the unmarked slow tests; it prints nothing when there are none.
func warnSlow(w io.Writer, slow []slowTest) {
	if len(slow) == 0 {
		return
	}
	fmt.Fprintf(w, "warning: %d test(s) took over %s under -short without a slowtest.Skip marker (not a failure):\n", len(slow), slowBudget)
	for _, s := range slow {
		fmt.Fprintf(w, "  %s took %s\n", s.name, s.elapsed.Round(time.Millisecond))
	}
}

// goTestShort runs go test, rendering -json as text and warning about
// unmarked slow tests after the run. Under -full nothing is slow by design,
// so it streams plain go test.
func goTestShort(dir string, pkgs ...string) error {
	if Full {
		return goRun(dir, testArgs(pkgs...)...)
	}
	args := append([]string{"test", "-json", "-short"}, pkgs...)
	pr, pw := io.Pipe()
	var slow []slowTest
	done := make(chan struct{})
	go func() {
		defer close(done)
		slow = renderTestJSON(pr, os.Stdout)
		io.Copy(io.Discard, pr)
	}()
	err := runOut(dir, pw, "go", args...)
	pw.Close()
	<-done
	warnSlow(os.Stdout, slow)
	return err
}
