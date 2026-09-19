package video

import (
	"encoding/json"
	"testing"
)

func TestLeasePacingMetadata(t *testing.T) {
	for _, tt := range []struct {
		name     string
		metadata string
		wantErr  bool
	}{
		{"hosted regression", `{"targetFrameRate":1,"refreshRate":1,"vsyncCount":1}`, true},
		{"virtual display", `{"targetFrameRate":60,"refreshRate":1,"vsyncCount":0}`, false},
		{"protobuf omitted zero", `{"targetFrameRate":60,"refreshRate":1}`, false},
		{"physical display", `{"targetFrameRate":60,"refreshRate":144,"vsyncCount":0}`, false},
		{"vsync still caps capture", `{"targetFrameRate":60,"refreshRate":1,"vsyncCount":1}`, true},
		{"frame limiter still caps capture", `{"targetFrameRate":1,"refreshRate":1}`, true},
		{"missing frame limiter", `{}`, true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			var state map[string]any
			if err := json.Unmarshal([]byte(tt.metadata), &state); err != nil {
				t.Fatal(err)
			}
			if err := assertLeasePacing(state); (err != nil) != tt.wantErr {
				t.Fatalf("assertLeasePacing() = %v, want error %v", err, tt.wantErr)
			}
		})
	}
}
