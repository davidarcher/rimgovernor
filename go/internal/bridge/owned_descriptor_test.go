package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/wire/placementpreview"
)

func TestPlacementRawObjectDescriptor(t *testing.T) {
	actual := `{"additionalProperties":false,"properties":{"placements":{"description":"Raw transport value; must be a JSON string containing the canonical placement array.","type":"object"}},"type":"object"}`
	s := &testServer{schema: actual}
	c := testClient(t, s, time.Second)
	valid := placementpreview.PlacementPreviewArguments{Placements: `[{"defName":"Wall","x":0,"z":0,"rotation":"North","stuff":""}]`}
	if _, err := c.PlacementPreviews(context.Background(), valid); err != nil {
		t.Fatal(err)
	}
	if len(s.calls) != 1 || s.calls[0].Tool != "home/placement_previews" {
		t.Fatalf("calls=%+v", s.calls)
	}
	if _, err := c.PlacementPreviews(context.Background(), placementpreview.PlacementPreviewArguments{Placements: `[{"defName":"Wall","x":0,"z":0,"godMode":true}]`}); !errors.Is(err, ErrContract) || len(s.calls) != 1 {
		t.Fatalf("canonical validation bypassed: %v", err)
	}
	for _, schema := range []string{
		`{"additionalProperties":false,"properties":{"placements":{"type":"object"},"apply":{"type":"boolean"}},"type":"object"}`,
		`{"additionalProperties":false,"properties":{"request":{"type":"object"}},"type":"object"}`,
		`{"additionalProperties":false,"properties":{"placements":{"type":"array"}},"type":"object"}`,
		`{"properties":{"placements":{"type":"object"}},"type":"object"}`,
		`{"additionalProperties":false,"properties":{"placements":{"type":"object","$ref":"https://example.invalid"}},"type":"object"}`,
	} {
		t.Run(schema, func(t *testing.T) {
			s := &testServer{schema: schema}
			c := testClient(t, s, time.Second)
			if _, err := c.PlacementPreviews(context.Background(), valid); !errors.Is(err, ErrContract) || len(s.calls) != 0 {
				t.Fatalf("unexpected descriptor accepted: %v", err)
			}
		})
	}
}
