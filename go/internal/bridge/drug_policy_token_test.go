package bridge

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// TestNativeDrugPolicyTokenIsPawnScoped guards #685: every drug-policy Apply
// calls SetDefault, so a token that hashes the colony default policy moves
// every sibling pawn's settings token on the first write and holds their work.
func TestNativeDrugPolicyTokenIsPawnScoped(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "src", "Bridge", "Protocol", "NativeDrugPolicy.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := string(data)
	if !strings.Contains(src, "db.SetDefault(") {
		t.Fatal("Apply no longer moves the default; revisit this guard")
	}
	body := regexp.MustCompile(`(?s)static string Token\(string work, Pawn pawn\)\s*\{(.*?)\n\s*\}`).FindStringSubmatch(src)
	if body == nil {
		t.Fatal("NativeDrugPolicy.Token not found")
	}
	if strings.Contains(body[1], "DefaultDrugPolicy") {
		t.Fatalf("Token hashes the colony default policy:\n%s", body[1])
	}
	if !strings.Contains(body[1], "Name(pawn)") {
		t.Fatalf("Token must hash the pawn's policy name:\n%s", body[1])
	}
}
