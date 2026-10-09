package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWorkHoursPerDayCountsWorkAndAnythingAndKeepsUnknown(t *testing.T) {
	slots := make([]string, 24)
	for i := range slots {
		switch {
		case i < 6:
			slots[i] = ScheduleSleep
		case i < 8:
			slots[i] = ScheduleJoy
		case i < 12:
			slots[i] = ScheduleWork
		default:
			slots[i] = ScheduleAnything
		}
	}
	if hours, known := BuildProfile(WorkPawn{Schedule: domain.Known(slots)}).WorkHours.Value(); !known || hours != 16 {
		t.Fatalf("hours = %d known %v", hours, known)
	}
	slots[0] = "Custom"
	if _, known := (WorkPawn{Schedule: domain.Known(slots)}).WorkHoursPerDay().Value(); known {
		t.Fatal("unclassified assignment must stay unknown")
	}
	if _, known := (WorkPawn{}).WorkHoursPerDay().Value(); known {
		t.Fatal("unread schedule must stay unknown")
	}
	if _, known := (WorkPawn{Schedule: domain.Known(slots[:23])}).WorkHoursPerDay().Value(); known {
		t.Fatal("short schedule must stay unknown")
	}
}
