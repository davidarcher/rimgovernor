package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestSkillPassionNoneIsNotPassionate(t *testing.T) {
	if got := skillPassion("Minor"); got != "Minor" {
		t.Fatalf("skillPassion(Minor) = %q", got)
	}
	s := policy.ProfileSkill{Name: "Artistic", Level: 3, Passion: skillPassion("None")}
	p := policy.PawnProfile{ID: "p1", Skills: map[string]policy.ProfileSkill{"Artistic": s}, Incapable: map[policy.WorkType]bool{}}
	if policy.Artist(p) {
		t.Fatal(`a "None" passion at Artistic 3 qualified as an artist`)
	}
}
