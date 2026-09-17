package bridge

import (
	"encoding/json"
	"testing"
)

func TestNativeTimingReadsOnlyCompleteNonNegativeSplits(t *testing.T) {
	cases := map[string]struct {
		raw     string
		queue   float64
		execute float64
		ok      bool
	}{
		"legacy wrapper":  {raw: `{"payload":"{}","operation":{"id":"x"}}`},
		"complete":        {raw: `{"payload":"{}","timing":{"queueMs":2.5,"executeMs":0.75}}`, queue: 2.5, execute: 0.75, ok: true},
		"missing execute": {raw: `{"payload":"{}","timing":{"queueMs":2.5}}`},
		"negative":        {raw: `{"payload":"{}","timing":{"queueMs":-1,"executeMs":1}}`},
		"not an object":   {raw: `{"payload":"{}","timing":3}`},
		"empty":           {raw: ``},
	}
	for name, tc := range cases {
		queue, execute, ok := nativeTiming(json.RawMessage(tc.raw))
		if ok != tc.ok || queue != tc.queue || execute != tc.execute {
			t.Fatalf("%s: got %v %v %v", name, queue, execute, ok)
		}
	}
}

func TestDecodePayloadAcceptsCompanionTiming(t *testing.T) {
	payload, err := decodePayload([]byte(`{"payload":"{}","timing":{"queueMs":1,"executeMs":2}}`))
	if err != nil || string(payload) != "{}" {
		t.Fatalf("timed wrapper rejected: %q %v", payload, err)
	}
	if _, err = decodePayload([]byte(`{"payload":"{}","phases":{}}`)); err == nil {
		t.Fatal("unknown wrapper field accepted")
	}
}
