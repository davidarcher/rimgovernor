package archgate

import (
	"os"
	"sort"
	"strconv"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// recordedConstantValues is every distinctive numeric value in the recorded
// catalog's GameConstants, formatted as the list file holds it.
func recordedConstantValues(t *testing.T) string {
	t.Helper()
	set := map[float64]bool{}
	var walk func(m protoreflect.Message)
	add := func(fd protoreflect.FieldDescriptor, v protoreflect.Value) {
		var x float64
		switch fd.Kind() {
		case protoreflect.Int32Kind, protoreflect.Int64Kind, protoreflect.Sint32Kind, protoreflect.Sint64Kind, protoreflect.Sfixed32Kind, protoreflect.Sfixed64Kind:
			x = float64(v.Int())
		case protoreflect.Uint32Kind, protoreflect.Uint64Kind, protoreflect.Fixed32Kind, protoreflect.Fixed64Kind:
			x = float64(v.Uint())
		case protoreflect.FloatKind:
			x, _ = strconv.ParseFloat(strconv.FormatFloat(v.Float(), 'g', -1, 32), 64)
		case protoreflect.DoubleKind:
			x = v.Float()
		default:
			return
		}
		if distinctiveConstant(x) {
			set[x] = true
		}
	}
	walk = func(m protoreflect.Message) {
		m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			switch {
			case fd.IsList() && fd.Message() != nil:
				for i := 0; i < v.List().Len(); i++ {
					walk(v.List().Get(i).Message())
				}
			case fd.IsList():
				for i := 0; i < v.List().Len(); i++ {
					add(fd, v.List().Get(i))
				}
			case fd.IsMap():
			case fd.Message() != nil:
				walk(v.Message())
			default:
				add(fd, v)
			}
			return true
		})
	}
	walk(testkit.RecordedCatalogWire(t).GameConstants.ProtoReflect())
	values := make([]float64, 0, len(set))
	for x := range set {
		values = append(values, x)
	}
	sort.Float64s(values)
	var sb strings.Builder
	for _, x := range values {
		sb.WriteString(strconv.FormatFloat(x, 'g', -1, 64) + "\n")
	}
	return sb.String()
}

func TestGameConstantsListIsFresh(t *testing.T) {
	want := recordedConstantValues(t)
	if os.Getenv("RG_UPDATE_GOLDEN") == "1" {
		if err := os.WriteFile("game_constants.txt", []byte(want), 0o644); err != nil {
			t.Fatal(err)
		}
		return
	}
	if gameConstantValues != want {
		t.Fatal("game_constants.txt differs from the recorded catalog's GameConstants; run `RG_UPDATE_GOLDEN=1 go test ./internal/archgate -run TestGameConstantsListIsFresh` from go/")
	}
}

func TestDistinctiveConstant(t *testing.T) {
	for x, want := range map[float64]bool{2500: true, 60000: true, 1000: false, 10000: false, 100000: false, 1024: false, 4096: false, 15: false, 0.5: false, 2500.5: false, 999: false} {
		if distinctiveConstant(x) != want {
			t.Errorf("distinctiveConstant(%v) = %v, want %v", x, !want, want)
		}
	}
}

func TestRule6FailsOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/buildingruntime/a.go": "package buildingruntime\nfunc f(x int) bool {\n\t_ = \"WoodLog\"\n\t_ = \"notADef\"\n\treturn x > 2500 || x > 7\n}\n",
		"internal/policy/b.go":          "package policy\nvar gearValuables = map[string]bool{\"Silver\": true}\nvar other = \"Steel\"\n",
		"internal/domain/game_time.go":  "package domain\nconst TicksPerDay = 60000\n",
		"internal/nativeaccept/c.go":    "package nativeaccept\nvar _ = \"Wall\"\n",
		"internal/policy/d_test.go":     "package policy\nvar _ = \"Wall\"\n",
	})
	got := Rule6(root)
	want := []string{
		"internal/buildingruntime/a.go|f|\"WoodLog\"",
		"internal/buildingruntime/a.go|f|2500",
		"internal/policy/b.go|-|\"Steel\"",
	}
	if len(got) != len(want) {
		t.Fatalf("rule 6 got %v, want %v", got, want)
	}
	for _, w := range want {
		if !has(got, w) {
			t.Errorf("rule 6 missing %q in %v", w, got)
		}
	}
}
