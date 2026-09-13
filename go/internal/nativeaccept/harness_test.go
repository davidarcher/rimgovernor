package nativeaccept

import "testing"

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
