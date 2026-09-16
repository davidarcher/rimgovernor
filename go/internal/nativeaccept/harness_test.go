package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

func TestValidateDiscoveryAcceptsExactProductionAndFixtures(t *testing.T) {
	names := []string{"rimgovernor/lifecycle_read_identity", "rimgovernor/authority_read_status", "test/b04f_setup"}
	production := map[string]bool{"rimgovernor/lifecycle_read_identity": true, "rimgovernor/authority_read_status": true}
	fixtures := map[string]bool{"test/b04f_setup": true}
	if err := ValidateDiscovery(names, production, fixtures, map[string]bool{"test/b04f_setup": true}); err != nil {
		t.Fatalf("expected success, got %v", err)
	}
}

func TestValidateDiscoveryRejectsDuplicates(t *testing.T) {
	names := []string{"rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_identity"}
	if err := ValidateDiscovery(names, nil, nil, nil); err == nil {
		t.Fatal("expected an error for a duplicate registration")
	}
}

func TestValidateDiscoveryRejectsMissingProduction(t *testing.T) {
	names := []string{"rimgovernor/lifecycle_read_identity"}
	production := map[string]bool{"rimgovernor/authority_read_status": true}
	if err := ValidateDiscovery(names, production, nil, nil); err == nil {
		t.Fatal("expected an error for a missing production export")
	}
}

func TestValidateDiscoveryRejectsMissingExpectedFixture(t *testing.T) {
	names := []string{"rimgovernor/lifecycle_read_identity"}
	if err := ValidateDiscovery(names, nil, nil, map[string]bool{"test/b04f_setup": true}); err == nil {
		t.Fatal("expected an error for a missing expected fixture")
	}
}

func TestValidateDiscoveryRejectsUnexpectedFixtureShapedExport(t *testing.T) {
	names := []string{"rimgovernor/lifecycle_read_identity", "test/unexpected_fixture"}
	if err := ValidateDiscovery(names, nil, nil, nil); err == nil {
		t.Fatal("expected an error for an unexpected test/-prefixed export")
	}
}

func TestValidateDiscoveryTreatsCasefoldedFixtureNameAsFixtureShaped(t *testing.T) {
	names := []string{"rimgovernor/some_fixture_tool"}
	if err := ValidateDiscovery(names, nil, nil, nil); err == nil {
		t.Fatal("expected an error for a name containing \"fixture\" with no matching expectation")
	}
}

// batchLog/normalLog are the startup-log fixtures: batch patches armed versus
// an ordinary graphical start.
const batchLog = "[HeadlessRim] Bootstrap armed.\n[HeadlessRim] Headless mode active."
const normalLog = "Normal game startup"

func TestCheckStartupLogRequiresBatchPatchesAndPreservesGraphicalMode(t *testing.T) {
	if err := CheckStartupLog(batchLog, true); err != nil {
		t.Fatalf("unexpected error for a genuine headless batch log: %v", err)
	}
	if err := CheckStartupLog(normalLog, false); err != nil {
		t.Fatalf("unexpected error for a genuine graphical log: %v", err)
	}
	cases := []struct {
		name     string
		log      string
		headless bool
	}{
		{"headless-markers-without-headless-launch", batchLog, false},
		{"empty-log-claiming-headless", "", true},
		{"bootstrap-error", batchLog + "[HeadlessRim] Bootstrap Error: missing target", true},
		{"post-init-error", batchLog + "[HeadlessRim] Post-Init Error: missing target", true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if err := CheckStartupLog(c.log, c.headless); err == nil {
				t.Fatalf("expected an error for %s", c.name)
			}
		})
	}
}

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
