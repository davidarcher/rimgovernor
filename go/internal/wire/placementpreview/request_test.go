package placementpreview

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

type requestCases struct {
	Version int           `json:"version"`
	Cases   []requestCase `json:"cases"`
}

type requestCase struct {
	ID     string `json:"id"`
	Target string `json:"target"`
	Input  struct {
		Kind         string `json:"kind"`
		JSON         string `json:"json"`
		Replacements []struct {
			Token string `json:"token"`
			Text  string `json:"text"`
			Count int    `json:"count"`
		} `json:"replacements"`
	} `json:"input"`
	Canonical struct {
		Accepted bool `json:"accepted"`
	} `json:"canonical"`
}

func TestSharedRequestBoundaries(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "..", "contracts", "fixtures", "placement-request-cases.json"))
	if err != nil {
		t.Fatal(err)
	}
	var cases requestCases
	if err := json.Unmarshal(data, &cases); err != nil {
		t.Fatal(err)
	}
	if cases.Version != 1 || len(cases.Cases) == 0 {
		t.Fatal("unsupported or empty fixture")
	}
	seen := make(map[string]bool)
	for _, c := range cases.Cases {
		if c.ID == "" || seen[c.ID] {
			t.Fatalf("invalid fixture ID %q", c.ID)
		}
		seen[c.ID] = true
		t.Run(c.ID, func(t *testing.T) {
			raw := c.Input.JSON
			switch c.Input.Kind {
			case "raw":
				if len(c.Input.Replacements) != 0 {
					t.Fatal("raw fixture has replacements")
				}
			case "template":
				for _, r := range c.Input.Replacements {
					if r.Token == "" || strings.Count(raw, r.Token) != 1 || r.Count < 0 || r.Count > 65536 {
						t.Fatal("invalid literal fixture expansion")
					}
					raw = strings.Replace(raw, r.Token, strings.Repeat(r.Text, r.Count), 1)
				}
			default:
				t.Fatalf("unsupported fixture kind %q", c.Input.Kind)
			}
			var err error
			switch c.Target {
			case "outer_arguments":
				_, err = DecodePlacementPreviewArguments([]byte(raw))
			case "placements":
				_, err = DecodePlacementBatch([]byte(raw))
			default:
				t.Fatalf("unsupported fixture target %q", c.Target)
			}
			if (err == nil) != c.Canonical.Accepted {
				t.Fatalf("accepted=%v, want %v; error=%v", err == nil, c.Canonical.Accepted, err)
			}
		})
	}
}

func TestRequestRejectsInvalidUTF8AndOversizedEvidence(t *testing.T) {
	for _, raw := range [][]byte{
		[]byte("{\"placements\":\"\xff\"}"),
		bytes.Repeat([]byte(" "), (1<<20)+1),
	} {
		if _, err := DecodePlacementPreviewArguments(raw); err == nil {
			t.Fatal("invalid or oversized evidence accepted")
		}
	}
}
