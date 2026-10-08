package ideology

import (
	"context"
	"encoding/json"
	"fmt"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"slices"
	"time"
)

func sortDesign(v *c.IdeoligionDesign) { slices.Sort(v.Memes); slices.Sort(v.Precepts) }

func init() {
	cases.Register(cases.Case{Name: "ideology/legal-reform", Scope: "#1662: a pinned eligible fluid ideoligion changes a plain precept through Actions/Apply, consumes development points once and increments its reform count once. A stale design is refused without mutation; an uncertain-result resend with a new key observes the applied target and never reforms twice. Snapshots cannot prove the native legality/apply/progression contract.", Start: cases.Fixture{Op: "test/ideoligion_design_prepare", On: cases.LabStart()}, Expansions: []string{"ludeon.rimworld.ideology"}, NoKeep: true, Quiet: na.QuietRequired, RequiredOps: []string{"test/ideoligion_design_prepare", "test/ideoligion_design_inspect"}, Budget: 5 * time.Minute, Crew: cases.Crew{Size: 3}, Run: runLegalReform})
}

func runLegalReform(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := h.Call(ctx, "prepare-design", "test/ideoligion_design_prepare", map[string]any{})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("prepare: %v", prepared)
	}
	var expected, design c.IdeoligionDesign
	if err := protojson.Unmarshal([]byte(na.AsString(prepared["expected"])), &expected); err != nil {
		return err
	}
	if err := protojson.Unmarshal([]byte(na.AsString(prepared["design"])), &design); err != nil {
		return err
	}
	count := int(na.AsNumber(prepared["count"]))
	inspect := func(label string, want *c.IdeoligionDesign, wantCount, wantPoints int) error {
		got, err := h.Call(ctx, label, "test/ideoligion_design_inspect", map[string]any{})
		if err != nil {
			return err
		}
		var actual c.IdeoligionDesign
		if err := protojson.Unmarshal([]byte(na.AsString(got["design"])), &actual); err != nil {
			return err
		}
		// Native sorts selections; canonicalize the requested set for comparison.
		canonical := proto.Clone(want).(*c.IdeoligionDesign)
		sortDesign(canonical)
		if !proto.Equal(&actual, canonical) || int(na.AsNumber(got["count"])) != wantCount || int(na.AsNumber(got["points"])) != wantPoints {
			return fmt.Errorf("%s: unexpected native state %v", label, got)
		}
		s.Report()[label] = got
		return nil
	}
	before, err := h.Call(ctx, "before", "test/ideoligion_design_inspect", map[string]any{})
	if err != nil {
		return err
	}
	points := int(na.AsNumber(before["points"]))
	send := func(key string, wantCount int, wantExpected, target *c.IdeoligionDesign) (map[string]any, error) {
		a, _ := protojson.Marshal(wantExpected)
		b, _ := protojson.Marshal(target)
		return h.Wire(ctx, key, "operations_apply", map[string]any{"identity": s.Identity(), "actions": []any{map[string]any{"key": key, "ideoligionReform": map[string]any{"ideoId": prepared["ideoId"], "expected": json.RawMessage(a), "design": json.RawMessage(b), "expectedReformCount": wantCount}}}})
	}
	stale := proto.Clone(&expected).(*c.IdeoligionDesign)
	stale.Precepts = append(stale.Precepts, "UnknownPrecept")
	reply, err := send("stale-reform", count, stale, &design)
	if err != nil {
		return err
	}
	rows := na.AsSlice(reply["results"])
	if len(rows) != 1 {
		return fmt.Errorf("stale reply: %v", reply)
	}
	row, _ := na.AsMap(rows[0])
	if _, ok := row["refused"]; !ok {
		return fmt.Errorf("stale request applied: %v", reply)
	}
	if err := inspect("stale-unchanged", &expected, count, points); err != nil {
		return err
	}
	for _, bad := range []string{"unknown", "duplicate", "missing-memes"} {
		invalid := proto.Clone(&design).(*c.IdeoligionDesign)
		switch bad {
		case "unknown":
			invalid.Precepts = append(invalid.Precepts, "UnknownPrecept")
		case "duplicate":
			invalid.Precepts = append(invalid.Precepts, invalid.Precepts[0])
		case "missing-memes":
			invalid.Memes = nil
		}
		reply, err := send(bad, count, &expected, invalid)
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("invalid reply %v", reply)
		}
		result, _ := na.AsMap(results[0])
		if _, refused := result["refused"]; !refused {
			return fmt.Errorf("invalid design applied: %v", reply)
		}
		if err := inspect(bad+"-unchanged", &expected, count, points); err != nil {
			return err
		}
	}
	for _, key := range []string{"legal-reform", "uncertain-resend-new-key"} {
		reply, err := send(key, count, &expected, &design)
		if err != nil {
			return err
		}
		rows := na.AsSlice(reply["results"])
		if len(rows) != 1 {
			return fmt.Errorf("apply: %v", reply)
		}
		row, _ := na.AsMap(rows[0])
		if _, ok := row["applied"]; !ok {
			return fmt.Errorf("reform refused: %v", reply)
		}
		if err := inspect(key+"-observed", &design, count+1, 0); err != nil {
			return err
		}
	}
	reply, err = send("ineligible-reform", count+1, &design, &expected)
	if err != nil {
		return err
	}
	rows = na.AsSlice(reply["results"])
	if len(rows) != 1 {
		return fmt.Errorf("ineligible reply %v", reply)
	}
	row, _ = na.AsMap(rows[0])
	if _, refused := row["refused"]; !refused {
		return fmt.Errorf("spent eligibility applied: %v", reply)
	}
	return inspect("ineligible-unchanged", &design, count+1, 0)
}
