package boundary

import (
	"testing"

	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestHourOfDayLongitudeZeroSanity(t *testing.T) {
	// round(0/15) == 0, so the offset is zero and hour tracks absTicks/2500
	// directly.
	for hour := 0; hour < 24; hour++ {
		ticks := int64(hour) * 2500
		if got := HourOfDay(ticks, 0); got != hour {
			t.Fatalf("hour %d: HourOfDay(%d, 0) = %d, want %d", hour, ticks, got, hour)
		}
	}
}

func TestHourOfDaySelfConsistentAcrossFullDay(t *testing.T) {
	// Every integer hour 0-23 must be reachable and monotonic (mod 24) as
	// ticks advance across a full day, for a representative set of
	// longitudes including non-zero offsets.
	for _, longitude := range []float64{0, 15, -15, 97.3, -180, 180} {
		prev := -1
		seenHours := map[int]bool{}
		for tick := int64(0); tick < 60000; tick += 2500 {
			hour := HourOfDay(tick, longitude)
			if hour < 0 || hour > 23 {
				t.Fatalf("longitude %v tick %d: hour %d out of range", longitude, tick, hour)
			}
			seenHours[hour] = true
			if prev != -1 {
				want := (prev + 1) % 24
				if hour != want {
					t.Fatalf("longitude %v tick %d: hour %d not monotonic after %d (want %d)", longitude, tick, hour, prev, want)
				}
			}
			prev = hour
		}
		if len(seenHours) != 24 {
			t.Fatalf("longitude %v: expected all 24 hours reachable, got %d", longitude, len(seenHours))
		}
	}
}

func TestHourOfDayNegativeLongitudeAndNegativeEffectiveTicks(t *testing.T) {
	// A negative longitude produces a negative offset; combined with a small
	// absTicks this drives x negative, exercising PositiveModRemap's x<0
	// branch. HourOfDay must stay in [0,23] and remain consistent with a
	// direct positiveModRemap computation for every case.
	longitude := -180.0 // TimeZoneAt(-180) = round(-12) = -12, offset = -30000
	for _, ticks := range []int64{0, 2500, 30000, -2500, -100000} {
		got := HourOfDay(ticks, longitude)
		if got < 0 || got > 23 {
			t.Fatalf("ticks %d longitude %v: hour %d out of range", ticks, longitude, got)
		}
		offset := timeZoneAt(longitude) * 2500
		want := positiveModRemap(ticks+offset, 2500, 24)
		if got != want {
			t.Fatalf("ticks %d longitude %v: HourOfDay = %d, want %d (from positiveModRemap directly)", ticks, longitude, got, want)
		}
	}
	// Cross-check the direct positiveModRemap port stays in range for a
	// spread of negative x values.
	for x := int64(-100000); x <= -1; x += 137 {
		got := positiveModRemap(x, 2500, 24)
		if got < 0 || got > 23 {
			t.Fatalf("positiveModRemap(%d,2500,24) = %d out of range", x, got)
		}
	}
}

func TestTimeZoneAtRoundsAwayFromZero(t *testing.T) {
	cases := []struct {
		longitude float64
		want      int64
	}{
		{0, 0},
		{7.5, 1},   // .5 boundary rounds away from zero
		{-7.5, -1}, // .5 boundary rounds away from zero
		{15, 1},
		{-15, -1},
		{97.3, 6},
		{-97.3, -6},
	}
	for _, c := range cases {
		if got := timeZoneAt(c.longitude); got != c.want {
			t.Fatalf("timeZoneAt(%v) = %d, want %d", c.longitude, got, c.want)
		}
	}
}

func TestExpectedScheduleDefSelectsMatchingHour(t *testing.T) {
	slots := []*n.TimetableSlot{
		{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Sleep")},
		{Hour: proto.Uint32(1), AssignmentDefName: proto.String("Sleep")},
	}
	def, ok := ExpectedScheduleDef(slots, 2500, 0) // hour 1 at longitude 0
	if !ok || def != "Sleep" {
		t.Fatalf("expected Sleep at hour 1, got %q ok=%v", def, ok)
	}
}

func TestExpectedScheduleDefUnknownCases(t *testing.T) {
	cases := [][]*n.TimetableSlot{
		nil,
		{},
		{{Hour: proto.Uint32(5), AssignmentDefName: proto.String("Sleep")}}, // no slot for hour 0
		{{Hour: nil, AssignmentDefName: proto.String("Sleep")}},
		{{Hour: proto.Uint32(0), AssignmentDefName: nil}},
		{{Hour: proto.Uint32(0), AssignmentDefName: proto.String("Sleep")}, {Hour: proto.Uint32(0), AssignmentDefName: proto.String("Anything")}}, // duplicate hour
		{nil},
	}
	for i, slots := range cases {
		if _, ok := ExpectedScheduleDef(slots, 0, 0); ok {
			t.Fatalf("case %d: expected unknown, got ok", i)
		}
	}
}
