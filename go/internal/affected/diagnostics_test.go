package affected

import (
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

const tracedSource = `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockSchedulerDebug {
		clockSchedulerLog("F: n=%d", n)
	}
	if n > 1 {
		return n
	} else if clockSchedulerDebug {
		clockSchedulerLog("F: small")
	}
	clockSchedulerLog("F: done")
	return 1
}
`

func TestDiagnosticOnly(t *testing.T) {
	cases := []struct {
		name string
		src  string
		want bool
	}{
		{"gate renamed and a trace reworded", `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockDebug() {
		clockSchedulerLog("F: n is %d", n)
	}
	if n > 1 {
		return n
	} else if clockDebug() {
		clockSchedulerLog("F: small")
	}
	return 1
}
`, false},
		{"trace with locals and a loop added", `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockDebug() && n > 0 {
		m, ok := n, true
		for i := 0; i < m; i++ {
			if ok {
				clockSchedulerLog("F: i=%d", i)
			}
		}
	}
	if n > 1 {
		return n
	}
	clockSchedulerLog("F: done")
	return 1
}
`, false},
		{"gated block assigns outward", `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockSchedulerDebug {
		n = 0
		clockSchedulerLog("F: n=%d", n)
	}
	if n > 1 {
		return n
	} else if clockSchedulerDebug {
		clockSchedulerLog("F: small")
	}
	clockSchedulerLog("F: done")
	return 1
}
`, false},
		{"gated block returns", `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockSchedulerDebug {
		return 0
	}
	if n > 1 {
		return n
	} else if clockSchedulerDebug {
		clockSchedulerLog("F: small")
	}
	clockSchedulerLog("F: done")
	return 1
}
`, false},
		{"code changed beside the trace", `package p

var clockSchedulerDebug = false

func clockDebug() bool { return clockSchedulerDebug }

func clockSchedulerLog(format string, args ...any) {}

func F(n int) int {
	if clockDebug() {
		clockSchedulerLog("F: n=%d", n)
	}
	if n > 2 {
		return n
	}
	return 1
}
`, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := diagnosticOnly([]byte(tracedSource), []byte(tc.src)); got != tc.want {
				t.Errorf("diagnosticOnly = %v, want %v", got, tc.want)
			}
		})
	}
}

func TestDiagnosticEffectsRemainSignificant(t *testing.T) {
	for _, statement := range []string{
		`clockSchedulerLog("%v", missingIdentifier)`,
		`clockSchedulerLog("%v", mutateState())`,
		`if clockDebug() { x := mutateState(); clockSchedulerLog("%v", x) }`,
		`if clockDebug() { var x = mutateState(); clockSchedulerLog("%v", x) }`,
		`if clockDebug() { x := state; x.Field = 1 }`,
		`if clockDebug() { x := state; x[0]++ }`,
		`if clockDebug() && mutateState() { clockSchedulerLog("ok") }`,
		`clockSchedulerLog("%v", <-events)`,
	} {
		t.Run(statement, func(t *testing.T) {
			after := strings.Replace(tracedSource, `clockSchedulerLog("F: done")`, statement, 1)
			if diagnosticOnly([]byte(tracedSource), []byte(after)) {
				t.Fatal("effectful edit filtered")
			}
		})
	}
	after := strings.Replace(tracedSource, `clockSchedulerLog("F: done")`, `clockSchedulerLog("finished")`, 1)
	if !diagnosticOnly([]byte(tracedSource), []byte(after)) {
		t.Fatal("literal trace edit should be acceptance-only")
	}
}

// The discovery -> selection -> compiler path must retain diagnostics even when
// acceptance can skip a literal-only trace edit.
func TestDiagnosticEditsKeepFastChecks(t *testing.T) {
	r := scratchRepo(t, "README.md", "fixture")
	for _, dir := range []string{"go/internal/buildingruntime", "go/internal/nativeaccept/cases/sample"} {
		if err := os.MkdirAll(filepath.Join(r, dir), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	const file = "go/internal/buildingruntime/trace.go"
	const source = `package buildingruntime
func clockSchedulerLog(format string, args ...any) {}
func F() { clockSchedulerLog("before") }
`
	write(t, r, "go/go.mod", "module example.test/checks\ngo 1.23\n")
	write(t, r, file, source)
	write(t, r, "go/internal/nativeaccept/cases/sample/sample.go", `package sample
import "example.test/checks/internal/buildingruntime"
func Run() { buildingruntime.F() }
`)
	gitRun(t, r, "add", ".")
	gitRun(t, r, "commit", "-qm", "fixture")
	gitRun(t, r, "branch", "base")
	for _, tc := range []struct {
		name, replacement string
		cases             bool
	}{
		{"literal", `clockSchedulerLog("after")`, false},
		{"undefined", `clockSchedulerLog("%v", missingIdentifier)`, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			write(t, r, file, strings.Replace(source, `clockSchedulerLog("before")`, tc.replacement, 1))
			changed, err := ChangedFiles(r, "base")
			if err != nil {
				t.Fatal(err)
			}
			if !slices.Equal(changed, []string{file}) {
				t.Fatalf("changed = %v", changed)
			}
			sel, err := Select(r, changed, "base")
			if err != nil {
				t.Fatal(err)
			}
			for _, pkg := range []string{"example.test/checks/internal/buildingruntime", "example.test/checks/internal/nativeaccept/cases/sample"} {
				if !slices.Contains(sel.Packages, pkg) {
					t.Errorf("fast checks missing %s: %+v", pkg, sel)
				}
			}
			if slices.Contains(sel.Cases, "sample") != tc.cases {
				t.Errorf("acceptance = %v", sel.Cases)
			}
			if tc.name == "undefined" {
				_, err := goOutput(filepath.Join(r, "go"), append([]string{"test"}, sel.Packages...)...)
				if err == nil || !strings.Contains(err.Error(), "undefined: missingIdentifier") {
					t.Fatalf("compiler did not reject undefined log argument: %v", err)
				}
			}
		})
	}
}
