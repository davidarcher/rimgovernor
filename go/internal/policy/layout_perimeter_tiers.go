package policy

import "regexp"

var corePerimeterTier = regexp.MustCompile(`^` + TierPerimeterPrefix + `(r\d+-)?\d+$`)

// IsCorePerimeterTier reports whether a tier is one of the perimeter wall's
// own sections (perimeter-NN, or perimeter-rN-NN after a re-cut), not a
// pump, light, fence, bait or removal tier.
func IsCorePerimeterTier(name DefenseTierName) bool {
	return corePerimeterTier.MatchString(string(name))
}
