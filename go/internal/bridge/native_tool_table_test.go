package bridge

import (
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// nativeToolGaps lists rimgovernor/* names this package calls that the native
// mod does not yet implement, each tracked by an open issue. A name leaves
// this set when its native tool lands; a new caller must not add to it
// without an issue.
var nativeToolGaps = map[string]string{
	"rimgovernor/observations_list_wall_upgrade_sites": "https://github.com/davidarcher/rimgovernor/issues/78",
	"rimgovernor/presentation_notifications":           "https://github.com/davidarcher/rimgovernor/issues/79",
}

// TestEveryBridgeToolExistsNatively guards the bridge's call sites against
// the native tool table: a read the mod refuses fails every clock step of
// the routine families that depend on it (#77).
func TestEveryBridgeToolExistsNatively(t *testing.T) {
	root := filepath.Join("..", "..", "..")
	native := filepath.Join(root, "integrations", "rimgovernor-native", "src")
	if _, err := os.Stat(native); err != nil {
		t.Skip("native source tree not present:", err)
	}
	called := scanNames(t, ".", `\.(?:protoRead|protoCall|clockCall)\(\w+, "(rimgovernor/[a-z_]+)"`, func(name string) bool {
		return strings.HasSuffix(name, ".go") && !strings.HasSuffix(name, "_test.go")
	})
	implemented := scanNames(t, native, `(?:\[Tool\(|ToolName = )"(rimgovernor/[a-z_]+)"`, func(name string) bool {
		return strings.HasSuffix(name, ".cs")
	})
	if len(called) < 40 || len(implemented) < 40 {
		t.Fatalf("tool scan looks wrong: %d called, %d implemented", len(called), len(implemented))
	}
	var missing []string
	for name := range called {
		if !implemented[name] && nativeToolGaps[name] == "" {
			missing = append(missing, name)
		}
	}
	sort.Strings(missing)
	if len(missing) != 0 {
		t.Fatalf("bridge calls native tools the mod does not implement: %v", missing)
	}
	for name, issue := range nativeToolGaps {
		if implemented[name] {
			t.Errorf("%s is implemented natively; drop it from nativeToolGaps (%s)", name, issue)
		}
		if !called[name] {
			t.Errorf("%s is no longer called; drop it from nativeToolGaps (%s)", name, issue)
		}
	}
}

func scanNames(t *testing.T, dir, pattern string, keep func(string) bool) map[string]bool {
	t.Helper()
	expression := regexp.MustCompile(pattern)
	found := map[string]bool{}
	err := filepath.WalkDir(dir, func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() || !keep(entry.Name()) {
			return err
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		for _, match := range expression.FindAllSubmatch(data, -1) {
			found[string(match[1])] = true
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	return found
}
