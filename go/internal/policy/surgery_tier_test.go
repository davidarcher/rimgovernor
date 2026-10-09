package policy

import "strings"

// testRecipeFacts are the body part facts vanilla's rows give (the bridge test
// TestBodyPartFactsMatchTheRecordedCatalog pins them): kidneys and lungs are
// harvested, a heart's removal kills.
var testRecipeFacts = RecipeFacts{HarvestOrgans: []string{"Kidney", "Lung"}, VitalParts: map[string]bool{"Heart": true}}

// testPartTier is the tier a recipe or hediff of vanilla's rows has, by name,
// for test operations built without a catalog (the bridge test
// TestPartTiersMatchTheRecordedCatalog pins the real derivation).
func testPartTier(name string) float64 {
	switch {
	case strings.Contains(name, "Archotech"):
		return 1.5
	case strings.Contains(name, "Bionic"):
		return 1.25
	case strings.Contains(name, "Natural"):
		return 1
	case strings.Contains(name, "Prosthetic"):
		return 0.85
	case strings.Contains(name, "Peg"), strings.Contains(name, "Wooden"), strings.Contains(name, "Denture"):
		return 0.6
	}
	return 0
}
