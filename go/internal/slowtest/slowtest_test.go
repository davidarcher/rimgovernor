package slowtest

import "testing"

type recorder struct {
	testing.TB
	skipped string
}

func (r *recorder) Helper()       {}
func (r *recorder) Skip(a ...any) { r.skipped = a[0].(string) }

func TestSkipFollowsShortMode(t *testing.T) {
	r := &recorder{}
	Skip(r, "because")
	if testing.Short() {
		if r.skipped != "slow: because" {
			t.Fatalf("skip message = %q", r.skipped)
		}
		return
	}
	if r.skipped != "" {
		t.Fatalf("skipped without -short: %q", r.skipped)
	}
}
