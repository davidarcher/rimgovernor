//go:build !windows

package bridge

import "os"

// gabsProcessLease is a no-op off Windows: GABS is killed by Close, and a
// controller that dies without Close leaves it running (see gabshttp.go).
type gabsProcessLease struct{}

func leaseGABSProcess(*os.Process) *gabsProcessLease { return nil }

func (l *gabsProcessLease) release() {}
