package remotebundle

import (
	"encoding/xml"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

func dependencyIdentity(tree string) (string, error) {
	inv, err := InventoryTree(tree)
	if err != nil {
		return "", err
	}
	var lines strings.Builder
	for _, f := range inv.Files {
		if !strings.HasPrefix(f.Path, "starts/") {
			fmt.Fprintf(&lines, "%s\t%d\t%s\n", f.Path, f.Bytes, f.SHA256)
		}
	}
	return Digest([]byte(lines.String())), nil
}

func checkGenerated(dir string, files []File) error {
	seen := map[string]bool{}
	for _, f := range files {
		if SafePath(f.Path) != nil || strings.Contains(f.Path, "/") || !strings.HasPrefix(f.Path, "RimGovernor-debug-") || !strings.HasSuffix(f.Path, ".rws") || seen[strings.ToLower(f.Path)] {
			return fmt.Errorf("invalid cached-start name %q", f.Path)
		}
		seen[strings.ToLower(f.Path)] = true
		p := filepath.Join(dir, f.Path)
		if err := VerifyFile(p, f.Bytes, f.SHA256); err != nil {
			return err
		}
		if err := coreSave(p); err != nil {
			return err
		}
	}
	return nil
}

func coreSave(filename string) error {
	f, err := os.Open(filename)
	if err != nil {
		return err
	}
	defer f.Close()
	d := xml.NewDecoder(io.LimitReader(f, 1<<20))
	for {
		tok, err := d.Token()
		if err != nil {
			return fmt.Errorf("cached-start metadata: %w", err)
		}
		start, ok := tok.(xml.StartElement)
		if !ok || start.Name.Local != "meta" {
			continue
		}
		var meta struct {
			Mods []string `xml:"modIds>li"`
		}
		if err := d.DecodeElement(&meta, &start); err != nil {
			return err
		}
		core := false
		for _, id := range meta.Mods {
			switch id {
			case "ludeon.rimworld":
				core = true
			case "brrainz.harmony", "brrainz.rimbridgeserver", "davidarcher.rimgovernor.native":
			default:
				return fmt.Errorf("cached start %s has unsupported mod %s", filepath.Base(filename), id)
			}
		}
		if !core {
			return fmt.Errorf("cached start lacks Core metadata")
		}
		return nil
	}
}

func StageStarts(tree, root, status string) error {
	if status != "compatible generated starts ready to stage" {
		return nil
	}
	b, err := os.ReadFile(filepath.Join(tree, "starts", "compatibility.json"))
	if err != nil {
		return err
	}
	var s Starts
	if err := Decode(b, &s); err != nil {
		return err
	}
	for _, f := range s.Generated {
		for _, profile := range []string{"profile", "headless-profile"} {
			if err := Materialize(filepath.Join(tree, "starts", f.Path), filepath.Join(root, profile, "Saves", f.Path)); err != nil {
				return err
			}
		}
	}
	return nil
}
