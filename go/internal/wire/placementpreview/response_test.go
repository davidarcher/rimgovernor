package placementpreview

import (
	"encoding/json"
	"strings"
	"testing"
)

const evaluated = `{"success":true,"canPlace":true,"madeFromStuff":true,"passability":"Impassable","isDoor":false,"researchFinished":true,"buildableByPlayer":true,"costList":[{"defName":"WoodLog","count":5}],"materials":{"unreadable":true,"rows":[{"defName":"WoodLog","available":null}]},"rotations":[{"rotation":"north","accepted":true,"reason":"","occupiedCells":[{"x":1,"z":2}],"blockingThings":[]}]}`

func TestPreviewReplyCurrentFacts(t *testing.T) {
	for _, candidate := range []string{evaluated, `{"success":false,"error":"unknown definition"}`} {
		raw := `{"success":true,"version":2,"tick":123,"mapId":0,"results":[` + candidate + `]}`
		v, err := DecodePreviewReply([]byte(raw))
		if err != nil {
			t.Fatal(err)
		}
		if v.PreviewBatch == nil || len(v.PreviewBatch.Results) != 1 {
			t.Fatal("batch lost")
		}
		encoded, err := json.Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = DecodePreviewReply(encoded); err != nil {
			t.Fatal(err)
		}
		if v.PreviewBatch.Results[0].PreviewEvaluated != nil && v.PreviewBatch.Results[0].PreviewEvaluated.Materials.Rows[0].Available != nil {
			t.Fatal("unknown stock became known")
		}
	}
	if _, err := DecodePreviewReply([]byte(`{"success":false,"error":"request refused"}`)); err != nil {
		t.Fatal(err)
	}
}

func TestPreviewRefusesIncompleteOrMalformedFacts(t *testing.T) {
	for _, bad := range []string{
		strings.Replace(evaluated, `"available":null`, `"available":-1`, 1),
		strings.Replace(evaluated, `"available":null`, `"available":1.0`, 1),
		strings.Replace(evaluated, `,"available":null`, "", 1),
		strings.Replace(evaluated, `"available":null`, `"available":null,"available":3`, 1),
		strings.Replace(evaluated, `"north"`, `"diagonal"`, 1),
		strings.Replace(evaluated, `"Impassable"`, `"unknown"`, 1),
		strings.Replace(evaluated, `"success":true`, `"success":false`, 1),
		strings.Replace(evaluated, `"blockingThings":[]`, `"blockingThings":null`, 1),
		strings.Replace(evaluated, `[{"x":1,"z":2}]`, `[]`, 1),
		`{"success":true,"error":"not evaluated"}`, `{"success":false}`, `null`,
	} {
		if _, err := DecodePreviewCandidate([]byte(bad)); err == nil {
			t.Fatalf("accepted invalid candidate: %s", bad)
		}
	}
	for _, bad := range []string{`{"success":true,"version":1,"tick":0,"mapId":0,"results":[]}`, `{"success":false,"error":"no","results":[]}`} {
		if _, err := DecodePreviewReply([]byte(bad)); err == nil {
			t.Fatal("invalid reply accepted")
		}
	}
}

func TestPreviewUnionSerializationRequiresExactlyOneValidBranch(t *testing.T) {
	for _, v := range []PreviewCandidate{{}, {PreviewFailure: &PreviewFailure{Success: true, Error: "invalid constant"}}, {PreviewFailure: &PreviewFailure{Error: "failure"}, PreviewEvaluated: &PreviewEvaluated{}}} {
		if _, err := json.Marshal(v); err == nil {
			t.Fatal("invalid variant serialized")
		}
	}
}
