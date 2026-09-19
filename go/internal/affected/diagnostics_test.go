package affected

import (
	"os/exec"
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
`, true},
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
`, true},
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

// #303 regated the trace in twelve buildingruntime files; the routine
// files changed nothing else and drop out of the changed set, while
// clock_scheduler.go (the gate itself moved) stays in.
func TestDiagnosticOnlyOn303(t *testing.T) {
	r := repo(t)
	const landing = "36443e7e"
	if err := exec.Command("git", "-C", r, "cat-file", "-e", landing+"^{commit}").Run(); err != nil {
		t.Skip("landing commit of #303 not in this checkout")
	}
	show := func(rev, file string) []byte {
		t.Helper()
		out, err := exec.Command("git", "-C", r, "show", rev+":go/internal/buildingruntime/"+file).Output()
		if err != nil {
			t.Fatal(err)
		}
		return out
	}
	for file, want := range map[string]bool{
		"routine_flooring.go":        true,
		"routine_lighting.go":        true,
		"routine_refrigeration.go":   true,
		"routine_comfort.go":         true,
		"routine_hospital.go":        true,
		"routine_routes.go":          true,
		"routine_sleeping.go":        true,
		"routine_sleeping_upkeep.go": true,
		"clock_poll.go":              true,
		"worker.go":                  true,
		"clock_scheduler.go":         false,
	} {
		if got := diagnosticOnly(show(landing+"^", file), show(landing, file)); got != want {
			t.Errorf("%s: diagnosticOnly = %v, want %v", file, got, want)
		}
	}
}
