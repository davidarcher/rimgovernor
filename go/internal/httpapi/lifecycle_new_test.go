package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

const newColonyBody = `{"requestId":"new-1","timeoutMs":600000,"spec":{"scenario":"Crashlanded","colonistCount":8,"seed":"tribal8","biomes":["TemperateForest"],"flatTile":true,"difficulty":"Rough","storyteller":"RimGovernorQuiet","minTemperature":-10,"maxTemperature":30,"worldTemperature":"Normal","mapSize":250,"planetCoverage":0.3,"saveName":"tribal8"}}`

func newColonyBodyWith(t *testing.T, edit func(spec map[string]any, top map[string]any)) string {
	t.Helper()
	var top map[string]any
	if err := json.Unmarshal([]byte(newColonyBody), &top); err != nil {
		t.Fatal(err)
	}
	edit(top["spec"].(map[string]any), top)
	out, err := json.Marshal(top)
	if err != nil {
		t.Fatal(err)
	}
	return string(out)
}

func TestLifecycleNewColonyCompleted(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	out := playerCall(s, "POST", "/api/lifecycle/new", newColonyBody, token)
	if out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	var dto newColonyCompletedDTO
	if err := json.Unmarshal(out.Body.Bytes(), &dto); err != nil || dto.Status != "completed" || dto.RequestID != "new-1" || dto.SaveName != "tribal8" ||
		!dto.Paused || dto.Identity.ColonyID != "colony" || dto.Tick != 42 || dto.ByteLength != 2048 || dto.Seed != "tribal8" {
		t.Fatal(out.Body.String(), err)
	}
	spec := f.seenNew.GetSpec()
	if f.seenNew.GetRequestId() != "new-1" || f.seenNew.GetTimeoutMs() != 600000 || spec.GetScenario() != "Crashlanded" || spec.GetColonistCount() != 8 ||
		spec.GetSeed() != "tribal8" || len(spec.GetBiomes()) != 1 || !spec.GetFlatTile() || spec.GetDifficulty() != "Rough" || spec.GetStoryteller() != "RimGovernorQuiet" ||
		spec.GetMinTemperature() != -10 || spec.GetMaxTemperature() != 30 || spec.GetWorldTemperature() != "Normal" || spec.GetMapSize() != 250 ||
		spec.GetPlanetCoverage() != 0.3 || spec.GetSaveName() != "tribal8" {
		t.Fatal(f.seenNew)
	}
}

func TestLifecycleNewColonyOmitsUnsetOptionals(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	body := newColonyBodyWith(t, func(spec, _ map[string]any) {
		delete(spec, "minTemperature")
		delete(spec, "maxTemperature")
		delete(spec, "worldTemperature")
		delete(spec, "biomes")
	})
	out := playerCall(s, "POST", "/api/lifecycle/new", body, token)
	if out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	spec := f.seenNew.GetSpec()
	if spec.MinTemperature != nil || spec.MaxTemperature != nil || spec.WorldTemperature != nil || len(spec.Biomes) != 0 {
		t.Fatal(spec)
	}
}

func TestLifecycleNewColonyPendingPolling(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	f.newColony = nil
	f.newErr = &bridge.NewColonyPending{Value: &l.NewColonyPending{
		RequestId: proto.String("new-1"), Phase: l.NewColonyPhase_NEW_COLONY_PHASE_ROLLING_COLONISTS.Enum(),
		Detail: proto.String("rerolling"), ElapsedMs: proto.Uint64(2500), RerollCount: proto.Uint32(7)}}
	for _, tc := range []struct {
		name, method, path, body string
		status                   int
	}{
		{"start", "POST", "/api/lifecycle/new", newColonyBody, 202},
		{"poll", "GET", "/api/lifecycle/new?requestId=new-1", "", 200},
	} {
		out := playerCall(s, tc.method, tc.path, tc.body, token)
		var dto newColonyPendingDTO
		if out.Code != tc.status || json.Unmarshal(out.Body.Bytes(), &dto) != nil || dto.Status != "pending" || dto.Phase != "rolling_colonists" ||
			dto.ElapsedMs != 2500 || dto.RerollCount != 7 || dto.Detail != "rerolling" || dto.RequestID != "new-1" {
			t.Fatal(tc.name, out.Code, out.Body.String())
		}
	}
	if f.readNewID != "new-1" {
		t.Fatal(f.readNewID)
	}
}

func TestLifecycleNewColonyReadCompleted(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "automate")
	out := playerCall(s, "GET", "/api/lifecycle/new?requestId=new-1", "", "")
	var dto newColonyCompletedDTO
	if out.Code != 200 || f.readNewID != "new-1" || json.Unmarshal(out.Body.Bytes(), &dto) != nil || dto.Status != "completed" || dto.SaveName != "tribal8" {
		t.Fatal(out.Code, out.Body.String())
	}
}

func TestLifecycleNewColonySuperseded(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	f.newColony = nil
	f.newErr = &bridge.NewColonySuperseded{Value: &l.NewColonySuperseded{RequestId: proto.String("new-1"), Detail: proto.String("newer request")}}
	for _, out := range []string{
		playerCall(s, "POST", "/api/lifecycle/new", newColonyBody, token).Body.String(),
		playerCall(s, "GET", "/api/lifecycle/new?requestId=new-1", "", "").Body.String(),
	} {
		if !strings.Contains(out, `"superseded"`) || !strings.Contains(out, "newer request") {
			t.Fatal(out)
		}
	}
	if out := playerCall(s, "GET", "/api/lifecycle/new?requestId=new-1", "", ""); out.Code != 409 {
		t.Fatal(out.Code)
	}
}

func TestLifecycleNewColonyNativeFailure(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	f.newColony = nil
	f.newErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("unknown scenario; valid: A, B")}}
	out := playerCall(s, "POST", "/api/lifecycle/new", newColonyBody, token)
	if out.Code != 400 || !strings.Contains(out.Body.String(), "unknown scenario; valid: A, B") || !strings.Contains(out.Body.String(), "native_invalid_request") {
		t.Fatal(out.Code, out.Body.String())
	}
	f.newErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String("not implemented")}}
	out = playerCall(s, "GET", "/api/lifecycle/new?requestId=new-1", "", "")
	if out.Code != 502 || !strings.Contains(out.Body.String(), "native_unavailable") {
		t.Fatal(out.Code, out.Body.String())
	}
}

func TestLifecycleNewColonyRequiresPlayerToken(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "automate")
	for _, token := range []string{"", "wrong-token"} {
		out := playerCall(s, "POST", "/api/lifecycle/new", newColonyBody, token)
		if out.Code != 403 || f.calls != 0 {
			t.Fatal(token, out.Code)
		}
	}
}

func TestLifecycleNewColonyValidation(t *testing.T) {
	for name, edit := range map[string]func(spec, top map[string]any){
		"missingRequestID":  func(_, top map[string]any) { delete(top, "requestId") },
		"timeoutTooSmall":   func(_, top map[string]any) { top["timeoutMs"] = 10 },
		"timeoutTooLarge":   func(_, top map[string]any) { top["timeoutMs"] = 99999999 },
		"missingSpec":       func(_, top map[string]any) { delete(top, "spec") },
		"unknownTopField":   func(_, top map[string]any) { top["extra"] = true },
		"unknownSpecField":  func(spec, _ map[string]any) { spec["extra"] = true },
		"missingScenario":   func(spec, _ map[string]any) { delete(spec, "scenario") },
		"zeroColonists":     func(spec, _ map[string]any) { spec["colonistCount"] = 0 },
		"elevenColonists":   func(spec, _ map[string]any) { spec["colonistCount"] = 11 },
		"mapSizeSmall":      func(spec, _ map[string]any) { spec["mapSize"] = 99 },
		"mapSizeLarge":      func(spec, _ map[string]any) { spec["mapSize"] = 401 },
		"coverageSmall":     func(spec, _ map[string]any) { spec["planetCoverage"] = 0.04 },
		"coverageLarge":     func(spec, _ map[string]any) { spec["planetCoverage"] = 1.5 },
		"coverageMissing":   func(spec, _ map[string]any) { delete(spec, "planetCoverage") },
		"minAboveMax":       func(spec, _ map[string]any) { spec["minTemperature"] = 40 },
		"saveNameTraversal": func(spec, _ map[string]any) { spec["saveName"] = "../escape" },
		"saveNameEmpty":     func(spec, _ map[string]any) { spec["saveName"] = "" },
		"duplicateBiome":    func(spec, _ map[string]any) { spec["biomes"] = []string{"A", "A"} },
	} {
		t.Run(name, func(t *testing.T) {
			s, f, token := lifecycleAPI(t, "automate")
			out := playerCall(s, "POST", "/api/lifecycle/new", newColonyBodyWith(t, edit), token)
			if out.Code != 400 || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String())
			}
		})
	}
}

func TestLifecycleNewColonyBoundaryValuesAccepted(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	body := newColonyBodyWith(t, func(spec, top map[string]any) {
		spec["colonistCount"], spec["mapSize"], spec["planetCoverage"], top["timeoutMs"] = 1, 100, 0.05, 1000
	})
	if out := playerCall(s, "POST", "/api/lifecycle/new", body, token); out.Code != 201 || f.calls != 1 {
		t.Fatal(out.Code, out.Body.String())
	}
	body = newColonyBodyWith(t, func(spec, top map[string]any) {
		spec["colonistCount"], spec["mapSize"], spec["planetCoverage"], top["timeoutMs"] = 10, 400, 1, 1800000
	})
	if out := playerCall(s, "POST", "/api/lifecycle/new", body, token); out.Code != 201 || f.calls != 2 {
		t.Fatal(out.Code, out.Body.String())
	}
}

func TestLifecycleNewColonyReadRequiresRequestID(t *testing.T) {
	s, f, _ := lifecycleAPI(t, "automate")
	for _, path := range []string{"/api/lifecycle/new", "/api/lifecycle/new?requestId=", "/api/lifecycle/new?requestId=a&requestId=b"} {
		if out := playerCall(s, "GET", path, "", ""); out.Code != 400 || f.calls != 0 {
			t.Fatal(path, out.Code, out.Body.String())
		}
	}
}

func TestLifecycleNewColonyRetriesAfterAttentionAck(t *testing.T) {
	s, f, fa, token := lifecycleAPIWithConfig(t, "automate", true, nil)
	f.err = attentionRefusal("attn_new")
	f.failCalls = 1
	out := playerCall(s, "POST", "/api/lifecycle/new", newColonyBody, token)
	if out.Code != 201 || f.calls != 2 || len(fa.acked) != 1 || fa.acked[0] != "attn_new" {
		t.Fatal(out.Code, out.Body.String(), f.calls, fa.acked)
	}
	f.calls, f.failCalls, fa.acked = 0, 1, nil
	out = playerCall(s, "GET", "/api/lifecycle/new?requestId=new-1", "", "")
	if out.Code != 200 || f.calls != 2 || len(fa.acked) != 1 {
		t.Fatal(out.Code, out.Body.String(), f.calls, fa.acked)
	}
}
