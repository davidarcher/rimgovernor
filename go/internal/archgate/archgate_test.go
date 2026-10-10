package archgate

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

func readBaseline(t *testing.T, rule int) map[string]bool {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("baseline", "rule"+strconv.Itoa(rule)+".txt"))
	if err != nil {
		t.Fatalf("arch rule %d: baseline: %v", rule, err)
	}
	out := map[string]bool{}
	for _, l := range strings.Split(string(b), "\n") {
		if l = strings.TrimSpace(l); l != "" && !strings.HasPrefix(l, "#") {
			out[l] = true
		}
	}
	return out
}

var gates = []struct {
	rule int
	msg  string
	fn   func(string) []string
}{
	{1, "import climbs the layer order or is a denied edge", Rule1},
	{2, "policy must be pure (no time, sync, runtime, os, net, goroutines)", Rule2},
	{3, "exported type has more than one owner among domain, policy, bridge, observation", Rule3},
	{4, "empty `if err != nil` swallows the error", Rule4},
	{5, "literal resource cap or sentinel standing for unknown", Rule5},
	{6, "quoted game def name or retyped game constant (read the catalog, or register a judgment table in policy.DefTables)", Rule6},
}

func TestGatesHoldOnMain(t *testing.T) {
	for _, g := range gates {
		for _, m := range Compare(g.rule, g.msg, g.fn(filepath.Join("..", "..")), readBaseline(t, g.rule)) {
			t.Error(m)
		}
	}
}

// write lays files (slash path -> source) under a fresh module root.
func write(t *testing.T, files map[string]string) string {
	t.Helper()
	root := t.TempDir()
	for p, src := range files {
		full := filepath.Join(root, filepath.FromSlash(p))
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(full, []byte(src), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return root
}

func has(got []string, want string) bool {
	for _, g := range got {
		if g == want {
			return true
		}
	}
	return false
}

func TestRule1FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/domain/a.go": "package domain\nimport _ \"" + modulePrefix + "policy\"\n",
		"internal/store/a.go":  "package store\nimport _ \"" + modulePrefix + "gabp\"\n",
		"internal/policy/a.go": "package policy\nimport _ \"" + modulePrefix + "domain\"\n",
	})
	got := Rule1(root)
	if !has(got, "domain->policy") || !has(got, "store->gabp") || has(got, "policy->domain") {
		t.Fatalf("rule 1 got %v", got)
	}
}

func TestRule2FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/policy/a.go": "package policy\nimport \"time\"\nfunc f() { go func() {}(); _ = time.Now() }\n",
		"internal/policy/b.go": "package policy\n",
	})
	got := Rule2(root)
	if !has(got, "internal/policy/a.go|import time") || !has(got, "internal/policy/a.go|go statement") || len(got) != 2 {
		t.Fatalf("rule 2 got %v", got)
	}
}

func TestRule3FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/domain/a.go":      "package domain\ntype PawnID int\ntype Only int\n",
		"internal/observation/a.go": "package observation\ntype PawnID string\ntype pawnID int\n",
	})
	got := Rule3(root)
	if len(got) != 1 || got[0] != "PawnID" {
		t.Fatalf("rule 3 got %v", got)
	}
}

func TestRule4FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/x/a.go": "package x\nfunc f() error {\n\tvar err error\n\tif err != nil {\n\t}\n\tif err != nil {\n\t\treturn err\n\t}\n\treturn nil\n}\n",
	})
	got := Rule4(root)
	if len(got) != 1 || got[0] != "internal/x/a.go|f" {
		t.Fatalf("rule 4 got %v", got)
	}
}

func TestRule5FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/policy/a.go": "package policy\nimport \"math\"\nfunc f(target, tick int) bool {\n\t_ = math.MaxFloat64\n\t_ = 1e12\n\t_ = 1 << 40\n\t_ = min(target, 10000)\n\treturn target > 10000 || tick > 10000\n}\n",
	})
	got := Rule5(root)
	for _, w := range []string{"math.MaxFloat64", "sentinel 1e+12", "sentinel 1.099511627776e+12", "10000 clamp", "10000 compared"} {
		if !has(got, "internal/policy/a.go|f|"+w) {
			t.Errorf("rule 5 missing %q in %v", w, got)
		}
	}
	if len(got) != 5 {
		t.Errorf("rule 5 got %v (the tick comparison must not count)", got)
	}
}

func TestCompareReportsNewAndStale(t *testing.T) {
	// A violation absent from the baseline and a baseline entry that is gone
	// are both failures that name the rule.
	msgs := Compare(4, "m", []string{"new"}, map[string]bool{"old": true})
	if len(msgs) != 2 || !strings.Contains(msgs[0], "arch rule 4 violated") || !strings.Contains(msgs[1], "arch rule 4: baseline entry") {
		t.Fatalf("msgs %v", msgs)
	}
}
