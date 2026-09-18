package facility

import "testing"

// The checkpoint is taken the first time EnsureComfort holds a development
// slot or a method; a sample held under startup_survival never qualifies.
func TestComfortAdmitted(t *testing.T) {
	t.Parallel()
	cases := []struct {
		name   string
		sample map[string]any
		want   bool
	}{
		{"unbound", map[string]any{"goal_bound": false, "method_count": 0}, false},
		{"held under startup", map[string]any{"method_count": 0, "development": map[string]any{"reason": "startup_survival", "selected": false}}, false},
		{"selected", map[string]any{"method_count": 0, "development": map[string]any{"reason": "", "selected": true}}, true},
		{"planned", map[string]any{"method_count": 1}, true},
	}
	for _, c := range cases {
		if got := comfortAdmitted(c.sample); got != c.want {
			t.Errorf("%s: comfortAdmitted=%v want %v", c.name, got, c.want)
		}
	}
}
