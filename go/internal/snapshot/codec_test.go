package snapshot

import (
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type codecInner struct {
	Name  string
	Count domain.Fact[int64]
}

type codecSample struct {
	Flag    domain.Fact[bool]
	Missing domain.Fact[float64]
	Rows    domain.Fact[[]codecInner]
	Cells   map[domain.Tick]domain.Fact[string]
	Named   map[string][]domain.Fact[int32]
	Ptr     *codecInner
	Arr     [2]domain.Fact[uint8]
	At      time.Time
	Build   []domain.Building
}

func TestCodecRoundTripsFacts(t *testing.T) {
	in := codecSample{
		Flag:  domain.Known(false),
		Rows:  domain.Known([]codecInner{{Name: "a", Count: domain.Known[int64](0)}, {Name: "b"}}),
		Cells: map[domain.Tick]domain.Fact[string]{-4: domain.Known(""), 9: domain.Unknown[string]()},
		Named: map[string][]domain.Fact[int32]{"x": {domain.Unknown[int32](), domain.Known[int32](3)}},
		Ptr:   &codecInner{Count: domain.Known[int64](7)},
		Arr:   [2]domain.Fact[uint8]{domain.Known[uint8](1)},
		At:    time.Date(2026, 9, 26, 1, 2, 3, 0, time.UTC),
	}
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 4}, domain.East, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	in.Build = []domain.Building{b}
	data, err := Encode(in)
	if err != nil {
		t.Fatal(err)
	}
	var out codecSample
	if err = Decode(data, &out); err != nil {
		t.Fatal(err, string(data))
	}
	if !reflect.DeepEqual(in, out) {
		t.Fatalf("%+v\n%+v\n%s", in, out, data)
	}
	again, err := Encode(out)
	if err != nil || string(again) != string(data) {
		t.Fatal("encoding is not canonical", err)
	}
}

func TestCodecRefusesWhatItCannotReplay(t *testing.T) {
	if _, err := Encode(struct{ Any any }{1}); err == nil {
		t.Fatal("interface encoded")
	}
	var out codecInner
	if err := Decode([]byte(`{"Renamed":1}`), &out); err == nil || !strings.Contains(err.Error(), "re-record") {
		t.Fatal(err)
	}
}
