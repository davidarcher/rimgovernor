package affected

import "testing"

func TestChangeClassification(t *testing.T) {
	for _, tc := range []struct {
		file          string
		goDir, probes bool
	}{
		{"docs/README.md", false, false},
		{"go/internal/store/store.go", true, false},
		{"integrations/rimgovernor-native/src/Changed.cs", false, true},
		{"contracts/tests/Stub.cs", false, true},
	} {
		if got := GoChanged([]string{tc.file}); got != tc.goDir {
			t.Errorf("%s: GoChanged = %v", tc.file, got)
		}
		if got := probesChanged([]string{tc.file}); got != tc.probes {
			t.Errorf("%s: probesChanged = %v", tc.file, got)
		}
	}
}
