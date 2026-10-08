package policy

// DeepDrillingResearch adds the extraction ladder only while a measured
// runway of a resource the catalog generates as a deep deposit is in deficit.
// Explicit operator research still takes precedence.
func DeepDrillingResearch(needs []string, runways []ResourceRunway, items ItemFacts) []string {
	for _, row := range runways {
		if !items.IsDeepResource(row.Resource) {
			continue
		}
		if deficit, known := row.Deficit.Value(); known && deficit {
			return append(append([]string(nil), needs...), "DeepDrilling", "GroundPenetratingScanner")
		}
	}
	return needs
}
