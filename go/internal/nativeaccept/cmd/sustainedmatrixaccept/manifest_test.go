package main

import (
	"encoding/json"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/variantgen"
)

// TestIssue1MatrixManifestValid guards the checked-in JSON spec against the
// same typo class a hand-run `-manifest` invocation would otherwise only
// discover after spending a native session on it: unknown/missing fields,
// out-of-range counts, or a seed left empty by an incomplete edit.
func TestIssue1MatrixManifestValid(t *testing.T) {
	data, err := os.ReadFile("manifests/issue-1-matrix.json")
	if err != nil {
		t.Fatal(err)
	}
	var variants []variantgen.Variant
	if err := json.Unmarshal(data, &variants); err != nil {
		t.Fatal(err)
	}
	if len(variants) == 0 {
		t.Fatal("manifest names no variants")
	}
	seen := map[string]bool{}
	for _, v := range variants {
		v = v.WithDefaults()
		if err := v.Validate(); err != nil {
			t.Errorf("variant %q: %v", v.Save, err)
		}
		if seen[v.Save] {
			t.Errorf("duplicate save name %q", v.Save)
		}
		seen[v.Save] = true
	}
}
