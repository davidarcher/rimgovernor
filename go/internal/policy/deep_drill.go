package policy

// DeepDrillingResearch adds the extraction ladder only while a measured metal
// runway is in deficit. Explicit operator research still takes precedence.
func DeepDrillingResearch(needs []string, runways []ResourceRunway) []string {
	for _, row := range runways {
		if row.Resource != "Steel" && row.Resource != "Plasteel" {
			continue
		}
		if deficit, known := row.Deficit.Value(); known && deficit {
			return append(append([]string(nil), needs...), "DeepDrilling", "GroundPenetratingScanner")
		}
	}
	return needs
}
