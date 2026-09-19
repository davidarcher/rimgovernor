package remotebundle

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/inputs"
)

const testCiphertext = "age-encryption.org/v1\nsynthetic-test-bytes"

func fixture(t *testing.T) (Manifest, Inventory) {
	t.Helper()
	inv := Inventory{1, []File{{"game/a", 3, Digest([]byte("abc"))}, {"game/sub/b", 0, Digest(nil)}}}
	m := Manifest{SchemaVersion: 1, Game: Game{"1.6", "windows-x64", true}, Origin: Origin{"owner/repo", 12}, Components: []Component{{Name: "game", Version: "1.6", PathPrefix: "game/"}}, Parts: []Part{{123, "part.age", int64(len(testCiphertext)), Digest([]byte(testCiphertext))}}, Inventory: Reference{"inventory.json", Digest(nil)}, Encryption: Encryption{"age-v1", "test"}}
	BindInventory(&m, inv)
	return m, inv
}

func TestManifestRejectsAmbiguousJSON(t *testing.T) {
	m, _ := fixture(t)
	b, _ := json.Marshal(m)
	for _, data := range []string{strings.Replace(string(b), `"schema_version":1`, `"schema_version":1,"schema_version":1`, 1), strings.Replace(string(b), `,"core_only":true`, ``, 1), strings.Replace(string(b), `"unpacked_bytes":3,`, ``, 1), string(b) + ` {}`, strings.Replace(string(b), `"components":[`, `"components":null,"unused":[`, 1)} {
		var got Manifest
		if err := Decode([]byte(data), &got); err == nil {
			t.Errorf("accepted invalid JSON: %s", data)
		}
	}
	var got Manifest
	if err := Decode(b, &got); err != nil {
		t.Fatal(err)
	}
	if err := got.Validate(true); err != nil {
		t.Fatal(err)
	}
}

func TestWindowsPaths(t *testing.T) {
	for _, p := range []string{"../x", "C:/x", "/x", `a\b`, "a:b", "a/../b", "a//b", "a.", "a ", "con.txt", "a/LPT1", "a\nfile", "."} {
		if SafePath(p) == nil {
			t.Errorf("accepted %q", p)
		}
	}
	for _, p := range []string{"game/Data/Core/a.xml", "game/a-b_1.dll"} {
		if err := SafePath(p); err != nil {
			t.Fatal(err)
		}
	}
}

func TestInventoryRejectsCorruption(t *testing.T) {
	for _, name := range []string{"case collision", "wrong component", "wrong size", "wrong digest", "unsorted", "file directory collision"} {
		t.Run(name, func(t *testing.T) {
			m, inv := fixture(t)
			switch name {
			case "case collision":
				inv.Files[1].Path = "game/A"
			case "wrong component":
				inv.Files[0].Path = "other/a"
			case "wrong size":
				inv.Files[0].Bytes++
			case "wrong digest":
				inv.Files[0].SHA256 = Digest(nil)
			case "unsorted":
				inv.Files[0], inv.Files[1] = inv.Files[1], inv.Files[0]
			case "file directory collision":
				inv.Files[1].Path = "game/a/b"
				BindInventory(&m, inv)
			}
			if ValidateInventory(inv, m) == nil {
				t.Fatal("accepted corrupt inventory")
			}
		})
	}
}

func TestTreeMissingExtraAndCorrupt(t *testing.T) {
	_, inv := fixture(t)
	root := t.TempDir()
	os.MkdirAll(filepath.Join(root, "game", "sub"), 0700)
	if VerifyTree(root, inv) == nil {
		t.Fatal("missing files accepted")
	}
	os.WriteFile(filepath.Join(root, "game", "a"), []byte("abc"), 0600)
	os.WriteFile(filepath.Join(root, "game", "sub", "b"), nil, 0600)
	if err := VerifyTree(root, inv); err != nil {
		t.Fatal(err)
	}
	os.WriteFile(filepath.Join(root, "player.xml"), nil, 0600)
	if VerifyTree(root, inv) == nil {
		t.Fatal("extra profile accepted")
	}
	os.Remove(filepath.Join(root, "player.xml"))
	os.WriteFile(filepath.Join(root, "game", "a"), []byte("bad"), 0600)
	if VerifyTree(root, inv) == nil {
		t.Fatal("corrupt file accepted")
	}
}

func TestArchivePreflight(t *testing.T) {
	_, inv := fixture(t)
	good := "Path = game\nFolder = +\n\nPath = game/a\nSize = 3\nAttributes = A\n\nPath = game/sub\nFolder = +\n\nPath = game/sub/b\nSize = 0\nAttributes = A\n"
	if err := ValidateListing(good, inv); err != nil {
		t.Fatal(err)
	}
	for _, bad := range []string{strings.Replace(good, "game/a", "../escape", 1), good + "\nPath = game/a\nSize = 3\n", strings.Replace(good, "Size = 3", "Size = 4", 1), strings.Replace(good, "Size = 3", "Size = 3\nSymbolic Link = C:\\outside", 1), strings.Replace(good, "Size = 3", "Size = 3\nHard Link = another", 1), strings.Replace(good, "Size = 3", "Size = 3\nAttributes = A l", 1)} {
		if ValidateListing(bad, inv) == nil {
			t.Errorf("accepted unsafe listing: %s", bad)
		}
	}
}

type fakeOrigin struct {
	downloads int
	wrong     bool
}

func (f *fakeOrigin) Release(context.Context, Origin) (Release, error) {
	id := int64(123)
	if f.wrong {
		id++
	}
	return Release{ID: 12, Assets: []Asset{{id, "part.age", int64(len(testCiphertext))}}}, nil
}
func (f *fakeOrigin) Download(_ context.Context, _ Origin, _ Part, w io.Writer) error {
	f.downloads++
	_, e := io.WriteString(w, testCiphertext)
	return e
}

func TestColdWarmRestoreAndCorruptHit(t *testing.T) {
	m, _ := fixture(t)
	cache := t.TempDir()
	digest := Digest([]byte("manifest"))
	client := &fakeOrigin{}
	for _, wantHit := range []bool{false, true} {
		hit, err := Restore(context.Background(), client, cache, digest, m)
		if err != nil || hit != wantHit {
			t.Fatalf("restore hit %v: %v", hit, err)
		}
	}
	if client.downloads != 1 {
		t.Fatalf("download count %d", client.downloads)
	}
	os.WriteFile(filepath.Join(cache, CacheKey(digest), "part.age"), []byte("bad"), 0600)
	if _, err := Restore(context.Background(), client, cache, digest, m); err == nil {
		t.Fatal("corrupt hit accepted")
	}
	if client.downloads != 1 {
		t.Fatal("silently redownloaded corrupt cache")
	}
	client.wrong = true
	if _, err := Restore(context.Background(), client, t.TempDir(), digest, m); err == nil {
		t.Fatal("asset from wrong origin accepted")
	}
}

func TestPartsRefuseMislabeledPlaintext(t *testing.T) {
	m, _ := fixture(t)
	root := t.TempDir()
	data := []byte("plaintext game archive")
	m.Parts[0].Bytes = int64(len(data))
	m.Parts[0].SHA256 = Digest(data)
	os.WriteFile(filepath.Join(root, m.Parts[0].Name), data, 0600)
	if VerifyParts(root, m) == nil {
		t.Fatal("plaintext accepted as encrypted bundle")
	}
}

func TestTrustRejectsUnreviewedInputs(t *testing.T) {
	sha := strings.Repeat("a", 40)
	tst := Trust{"owner/repo", sha, sha, "workflow_dispatch"}
	if err := tst.Validate("owner/repo", sha, "workflow_dispatch", sha); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][4]string{{"fork/repo", sha, "workflow_dispatch", sha}, {"owner/repo", strings.Repeat("b", 40), "workflow_dispatch", sha}, {"owner/repo", sha, "pull_request", sha}, {"owner/repo", sha, "workflow_dispatch", strings.Repeat("b", 40)}} {
		if tst.Validate(args[0], args[1], args[2], args[3]) == nil {
			t.Fatal("untrusted input accepted")
		}
	}
}

func TestGeneratedStartCompatibilityAndStaging(t *testing.T) {
	repo, err := filepath.Abs("../../..")
	if err != nil {
		t.Fatal(err)
	}
	native, err := inputs.SourceTreeHash(repo)
	if err != nil {
		t.Fatal(err)
	}
	fixtures, err := InventoryTree(filepath.Join(repo, "scripts", "fixtures", "saves"))
	if err != nil {
		t.Fatal(err)
	}
	tree := t.TempDir()
	starts := filepath.Join(tree, "starts")
	os.Mkdir(starts, 0700)
	name := "RimGovernor-debug-test.rws"
	save := []byte(`<savegame><meta><modIds><li>ludeon.rimworld</li><li>davidarcher.rimgovernor.native</li></modIds></meta></savegame>`)
	os.WriteFile(filepath.Join(starts, name), save, 0600)
	s := Starts{SchemaVersion: 1, GameVersion: "1.6", NativeSourceSHA256: native, DependenciesSHA256: Digest(nil), CommittedFixtures: fixtures.Files, Generated: []File{{name, int64(len(save)), Digest(save)}}}
	metadata := filepath.Join(starts, "compatibility.json")
	if err := WriteJSON(metadata, s); err != nil {
		t.Fatal(err)
	}
	m, _ := fixture(t)
	status, err := ValidateStarts(tree, repo, m)
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	if err := StageStarts(tree, root, status); err != nil {
		t.Fatal(err)
	}
	for _, profile := range []string{"profile", "headless-profile"} {
		if err := VerifyFile(filepath.Join(root, profile, "Saves", name), int64(len(save)), Digest(save)); err != nil {
			t.Fatal(err)
		}
	}
	s.NativeSourceSHA256 = Digest([]byte("older"))
	WriteJSON(metadata, s)
	status, err = ValidateStarts(tree, repo, m)
	if err != nil || !strings.Contains(status, "regenerate") {
		t.Fatalf("stale start: %s %v", status, err)
	}
	skip := t.TempDir()
	if err := StageStarts(tree, skip, status); err != nil {
		t.Fatal(err)
	}
	entries, _ := os.ReadDir(skip)
	if len(entries) != 0 {
		t.Fatal("stale save was staged")
	}
	s.Generated[0].Path = "unrelated-player-save.rws"
	WriteJSON(metadata, s)
	if _, err := ValidateStarts(tree, repo, m); err == nil {
		t.Fatal("unrelated save accepted")
	}
	os.WriteFile(filepath.Join(starts, name), []byte(`<savegame><meta><modIds><li>ludeon.rimworld</li><li>ludeon.rimworld.royalty</li></modIds></meta></savegame>`), 0600)
	if coreSave(filepath.Join(starts, name)) == nil {
		t.Fatal("DLC save accepted")
	}
}

func TestAuthorizationCannotComeFromTestedCheckout(t *testing.T) {
	repo := t.TempDir()
	auth := filepath.Join(repo, "trust.json")
	os.WriteFile(auth, []byte("{}"), 0600)
	if CheckAuthorizationPath(repo, auth) == nil {
		t.Fatal("checkout authorized itself")
	}
	outside := filepath.Join(t.TempDir(), "trust.json")
	os.WriteFile(outside, []byte("{}"), 0600)
	if err := CheckAuthorizationPath(repo, outside); err != nil {
		t.Fatal(err)
	}
}

// Opt-in boundary test uses actual age/7z executables and synthetic bytes only.
func TestEncryptedMultipartRoundTrip(t *testing.T) {
	age, zip := os.Getenv("RIMGOVERNOR_TEST_AGE"), os.Getenv("RIMGOVERNOR_TEST_7Z")
	if age == "" || zip == "" {
		t.Skip("set RIMGOVERNOR_TEST_AGE and RIMGOVERNOR_TEST_7Z for real-tool round trip")
	}
	root := t.TempDir()
	tree := filepath.Join(root, "tree")
	os.MkdirAll(filepath.Join(tree, "game"), 0700)
	os.WriteFile(filepath.Join(tree, "game", "hello-Català"), []byte("not licensed game data"), 0600)
	identity := filepath.Join(root, "identity.txt")
	keygen := filepath.Join(filepath.Dir(age), "age-keygen.exe")
	if out, err := exec.Command(keygen, "-o", identity).CombinedOutput(); err != nil {
		t.Fatalf("keygen: %s: %v", out, err)
	}
	pub, err := exec.Command(keygen, "-y", identity).Output()
	if err != nil {
		t.Fatal(err)
	}
	out := filepath.Join(root, "out")
	os.Mkdir(out, 0700)
	tools := Tools{zip, age}
	parts, err := EncryptArchive(context.Background(), tools, tree, out, strings.TrimSpace(string(pub)), 100)
	if err != nil {
		t.Fatal(err)
	}
	if len(parts) < 2 {
		t.Fatal("test did not split archive")
	}
	inv, err := InventoryTree(tree)
	if err != nil {
		t.Fatal(err)
	}
	m, _ := fixture(t)
	m.Parts = parts
	BindInventory(&m, inv)
	dest := filepath.Join(root, "clean")
	if err := Extract(context.Background(), tools, out, dest, identity, m, inv); err != nil {
		t.Fatal(err)
	}
	if err := VerifyTree(dest, inv); err != nil {
		t.Fatal(err)
	}
	os.Remove(filepath.Join(out, parts[0].Name))
	if err := Extract(context.Background(), tools, out, filepath.Join(root, "missing"), identity, m, inv); err == nil {
		t.Fatal("missing part accepted")
	}
}
