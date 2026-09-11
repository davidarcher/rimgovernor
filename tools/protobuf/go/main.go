// Command protobufproof checks official cross-language wire fixtures, not gameplay.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/wrapperspb"
)

func fixtures() map[string]proto.Message {
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	context := &c.ObservationContext{Identity: identity, Tick: proto.Int64(9223372036854775807), NativeGeneration: proto.Uint64(18446744073709551615)}
	request := &p.PlacementRequest{Identity: identity, Placements: []*p.PlacementCandidate{{DefName: proto.String("Wall"), X: proto.Int32(0), Z: proto.Int32(4), Rotation: p.Rotation_ROTATION_NORTH.Enum(), Stuff: proto.String("WoodLog")}}}
	materials := &p.PlacementMaterials{Availability: &p.PlacementMaterials_Known{Known: &p.MaterialRows{Rows: []*p.PlacementMaterialStock{{DefName: proto.String("WoodLog"), Available: proto.Int32(0)}, {DefName: proto.String("Steel")}}}}}
	evaluated := &p.PlacementEvaluated{CanPlace: proto.Bool(false), MadeFromStuff: proto.Bool(true), Materials: materials}
	reply := &p.PlacementReply{Outcome: &p.PlacementReply_Batch{Batch: &p.PlacementBatch{Context: context, Results: []*p.CandidateReply{{Outcome: &p.CandidateReply_Evaluated{Evaluated: evaluated}}}}}}
	return map[string]proto.Message{"request": request, "reply": reply, "u64": wrapperspb.UInt64(^uint64(0)), "context": context}
}
func decodePair(directory, name string, model proto.Message) (proto.Message, error) {
	j, err := os.ReadFile(filepath.Join(directory, name+".json"))
	if err != nil {
		return nil, err
	}
	b, err := os.ReadFile(filepath.Join(directory, name+".bin"))
	if err != nil {
		return nil, err
	}
	fromJSON := model.ProtoReflect().Type().New().Interface()
	fromBinary := model.ProtoReflect().Type().New().Interface()
	if err = protojson.Unmarshal(j, fromJSON); err != nil {
		return nil, err
	}
	if err = proto.Unmarshal(b, fromBinary); err != nil {
		return nil, err
	}
	if !proto.Equal(fromJSON, fromBinary) {
		return nil, fmt.Errorf("%s JSON/binary differ", name)
	}
	return fromJSON, nil
}
func writePair(directory, name string, message proto.Message) error {
	j, err := protojson.Marshal(message)
	if err != nil {
		return err
	}
	b, err := proto.Marshal(message)
	if err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(directory, name+".json"), j, 0600); err != nil {
		return err
	}
	if err = os.WriteFile(filepath.Join(directory, name+".bin"), b, 0600); err != nil {
		return err
	}
	decoded, err := decodePair(directory, name, message)
	if err != nil {
		return err
	}
	if !proto.Equal(decoded, message) {
		return fmt.Errorf("%s roundtrip differs", name)
	}
	return nil
}
func run(output, cross string, checkEcho bool) error {
	if err := os.Mkdir(output, 0700); err != nil {
		return fmt.Errorf("fresh output required: %w", err)
	}
	models := fixtures()
	for name, message := range models {
		if err := writePair(output, "go-"+name, message); err != nil {
			return err
		}
	}
	if cross != "" {
		// Independently produced C# fixtures are preserved. Go echoes receive separate
		// names and are never substituted for the originating C# coverage.
		origin := map[string]proto.Message{"request": &p.PlacementRequest{}, "reply": &p.PlacementReply{}, "u64": &wrapperspb.UInt64Value{}, "context": &c.ObservationContext{}, "authority-inactive": &a.Status{}, "authority-active": &a.Status{}, "authority-acquire": &a.ControlRequest{}}
		for name, model := range origin {
			message, err := decodePair(cross, "csharp-"+name, model)
			if err != nil {
				return err
			}
			if err = writePair(output, "csharp-echo-"+name, message); err != nil {
				return err
			}
			fmt.Println("verified independent C#", name)
		}
		if checkEcho {
			for name, model := range models {
				message, err := decodePair(cross, "go-echo-"+name, model)
				if err != nil {
					return err
				}
				if !proto.Equal(message, model) {
					return fmt.Errorf("Go/C#/Go mismatch: %s", name)
				}
				fmt.Println("verified Go/C#/Go", name)
			}
		}
	}
	if err := emitShapes(output); err != nil {
		return err
	}
	if cross != "" {
		if err := verifyShapeManifest(cross, output); err != nil {
			return err
		}
		if checkEcho {
			if err := verifyGoShapeEcho(cross, output); err != nil {
				return err
			}
		}
	}
	fmt.Println("Official generated Go Protobuf proof passed;", output)
	return nil
}
func main() {
	output := flag.String("output", "", "fresh proof artifact directory (parent must exist)")
	cross := flag.String("cross-language-inputs", "", "C# origin and optional Go-echo fixture directory")
	echo := flag.Bool("check-go-echo", false, "also verify C# re-emitted Go fixtures against their originals")
	flag.Parse()
	if *output == "" || (*echo && *cross == "") {
		fmt.Fprintln(os.Stderr, "--output required; --check-go-echo requires --cross-language-inputs")
		os.Exit(2)
	}
	if err := run(*output, *cross, *echo); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
