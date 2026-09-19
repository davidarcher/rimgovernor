//go:build !windows

package remotebundle

import "fmt"

func freeDisk(string) (uint64, error) { return 0, fmt.Errorf("runner preflight requires Windows") }
