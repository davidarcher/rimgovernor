package observation

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestGearClimateFacts(t *testing.T) {
	if GearClimateFacts(nil) != nil || GearClimateFacts(&o.GearSnapshot{}) != nil {
		t.Fatal("absent climate must stay unknown")
	}
	g := &o.GearSnapshot{OutdoorTemperatureByTwelfthC: make([]float32, 12), CurrentTwelfth: proto.Uint32(11), TicksToNextTwelfth: proto.Int32(20), ActiveWeather: &o.GearWeatherCondition{DefName: proto.String("HeatWave"), RemainingTicks: proto.Int64(21), TemperatureOffsetC: proto.Float32(17)}}
	g.OutdoorTemperatureByTwelfthC[0] = 30
	c := GearClimateFacts(g)
	if err := c.Validate(); err != nil {
		t.Fatal(err)
	}
	_, high := c.TemperatureRange(22)
	if high != 47 {
		t.Fatalf("decoded forecast: %+v, high %v", c, high)
	}
	g.OutdoorTemperatureByTwelfthC[0] = 100
	if c.Temperatures[0] != 30 {
		t.Fatal("adapter aliases wire curve")
	}
}
