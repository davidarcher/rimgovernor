package nativeaccept

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// writePackage writes a minimal installed native package.
func writePackage(t *testing.T, root string) {
	t.Helper()
	mod := filepath.Join(root, "Mods", "RimGovernor")
	files := map[string]string{
		filepath.Join("About", "About.xml"):                                   `<ModMetaData><packageId>` + NativePackage + `</packageId></ModMetaData>`,
		filepath.Join("Assemblies", "RimGovernor.Runtime.dll"):                "test assembly",
		filepath.Join("BridgeTools", "RimGovernor", "RimGovernor.Bridge.dll"): "test assembly",
	}
	for relative, content := range files {
		path := filepath.Join(mod, relative)
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatalf("mkdir %s: %v", path, err)
		}
		if err := os.WriteFile(path, []byte(content), 0644); err != nil {
			t.Fatalf("write %s: %v", path, err)
		}
	}
}

func TestPackageFilesRequiresBothLoaderAssembliesAndRejectsMixedInstall(t *testing.T) {
	root := t.TempDir()
	writePackage(t, root)
	hashes, err := PackageFiles(root)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(hashes) != 3 {
		t.Fatalf("expected 3 hashed files, got %d", len(hashes))
	}
	if err := os.MkdirAll(filepath.Join(root, "Mods", "RimGovernorHeadless"), 0755); err != nil {
		t.Fatalf("mkdir legacy package dir: %v", err)
	}
	if _, err := PackageFiles(root); err == nil {
		t.Fatal("expected an error for a mixed package installation")
	}
}

// TestOutcomeRequiresExactlyOneNamedCase:
// a decoded ProtoJSON reply must carry exactly the one requested oneof case, never an
// unrequested case alone or alongside it.
func TestOutcomeRequiresExactlyOneNamedCase(t *testing.T) {
	if _, value, err := Outcome(map[string]any{"batch": map[string]any{"results": []any{}}}, "batch"); err != nil {
		t.Fatalf("unexpected error: %v", err)
	} else if len(value) != 1 {
		t.Fatalf("expected the batch case's own object, got %#v", value)
	}
	cases := map[string]map[string]any{
		"only-failure-present":     {"failure": map[string]any{"code": "FAILURE_CODE_UNAVAILABLE"}},
		"both-batch-and-failure":   {"batch": map[string]any{}, "failure": map[string]any{}},
		"case-value-not-an-object": {"batch": "[]"},
	}
	for name, message := range cases {
		t.Run(name, func(t *testing.T) {
			if _, _, err := Outcome(message, "batch"); err == nil {
				t.Fatalf("expected an error for %s", name)
			}
		})
	}
}

// A lifecycle call's own timeoutMs sizes the bridge deadline: loading a save
// or generating a world legitimately outlasts an ordinary read, and cutting
// it at the session timeout reported a bounded wait as a transport failure.
func TestCoverNativeWaitRaisesTheDeadlineToTheRequestedWait(t *testing.T) {
	for _, tc := range []struct {
		args string
		want time.Duration
	}{
		{`{"readiness":"visual","timeoutMs":120000}`, 120*time.Second + nativeWaitMargin},
		{`{"timeoutMs":180000}`, 180*time.Second + nativeWaitMargin},
		{`{}`, 0},
		{`{"timeoutMs":0}`, 0},
		{`{"timeoutMs":"soon"}`, 0},
		{`not json`, 0},
	} {
		got, raised := bridge.CallTimeoutFrom(coverNativeWait(context.Background(), []byte(tc.args)))
		if tc.want == 0 {
			if raised {
				t.Fatalf("coverNativeWait(%s) raised the deadline to %v", tc.args, got)
			}
			continue
		}
		if got != tc.want {
			t.Fatalf("coverNativeWait(%s) = %v, wanted %v", tc.args, got, tc.want)
		}
	}
}
