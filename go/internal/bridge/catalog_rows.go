package bridge

import "math"

func catalogNumbers(kind string, values ...*float64) error {
	for _, v := range values {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0)) {
			return contract("nonfinite %s number", kind)
		}
	}
	return nil
}
