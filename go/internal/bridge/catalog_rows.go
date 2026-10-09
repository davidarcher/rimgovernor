package bridge

import "math"

// catalogIndex indexes a DLC catalog section's rows by name (kind names the DLC and the row, e.g. "odyssey biome"), refusing an invalid or duplicate one.
func catalogIndex[T any](kind string, rows []T, name func(T) string) (map[string]T, error) {
	out := make(map[string]T, len(rows))
	for _, row := range rows {
		n := name(row)
		if validID(n) != nil {
			return nil, contract("invalid %s name", kind)
		}
		if _, dup := out[n]; dup {
			return nil, contract("duplicate %s %s", kind, n)
		}
		out[n] = row
	}
	return out, nil
}

func catalogNumbers(kind string, values ...*float64) error {
	for _, v := range values {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0)) {
			return contract("nonfinite %s number", kind)
		}
	}
	return nil
}
