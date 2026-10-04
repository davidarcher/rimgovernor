// Package slowtest marks tests that are too slow for cmd/test's -short pass.
package slowtest

import "testing"

// Skip skips the test under -short with the message "slow: <reason>"; it
// does nothing otherwise.
func Skip(t testing.TB, reason string) {
	t.Helper()
	if testing.Short() {
		t.Skip("slow: " + reason)
	}
}
