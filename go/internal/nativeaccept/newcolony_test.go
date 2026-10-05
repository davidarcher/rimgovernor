package nativeaccept

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestSaveMapHashIgnoresIdsAndOrder(t *testing.T) {
	save := func(name, things string) string {
		path := filepath.Join(t.TempDir(), name)
		body := "<savegame><world><thing Class=\"Pawn\"><def>Human</def><pos>(1, 0, 1)</pos></thing></world><maps><li><terrainGrid>AAAA</terrainGrid><things>" + things + "</things></li></maps></savegame>"
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
		return path
	}
	thing := func(id, def, pos string) string {
		return "<thing Class=\"Building\"><def>" + def + "</def><id>" + id + "</id><pos>" + pos + "</pos></thing>"
	}
	a, err := SaveMapHash(save("a.rws", thing("Wall1", "Wall", "(2, 0, 3)")+thing("Tree9", "Plant_Oak", "(5, 0, 5)")))
	if err != nil {
		t.Fatal(err)
	}
	b, err := SaveMapHash(save("b.rws", thing("Tree77", "Plant_Oak", "(5, 0, 5)")+thing("Wall8", "Wall", "(2, 0, 3)")))
	if err != nil {
		t.Fatal(err)
	}
	if a != b {
		t.Fatalf("same things with other ids and order hash differently: %s vs %s", a, b)
	}
	c, err := SaveMapHash(save("c.rws", thing("Wall1", "Wall", "(2, 0, 4)")+thing("Tree9", "Plant_Oak", "(5, 0, 5)")))
	if err != nil {
		t.Fatal(err)
	}
	if a == c || !strings.HasSuffix(a, "/2-things") {
		t.Fatalf("a moved thing must change the digest: %s vs %s", a, c)
	}
}
