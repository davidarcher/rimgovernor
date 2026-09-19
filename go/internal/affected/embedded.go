package affected

import (
	"os"
	"path"
	"path/filepath"
	"slices"
	"strings"
)

type packageMetadata struct {
	ForTest, Name                                                          string
	ImportPath, Dir                                                        string
	Imports, Deps, TestImports, XTestImports                               []string
	EmbedFiles, EmbedPatterns                                              []string
	TestEmbedFiles, TestEmbedPatterns, XTestEmbedFiles, XTestEmbedPatterns []string
	Error                                                                  *packageError
	DepsErrors                                                             []packageError
	Module                                                                 *struct{ Path string }
}

type packageError struct{ Err string }

func missingEmbed(err string) bool {
	return strings.HasPrefix(err, "pattern ") && strings.HasSuffix(err, ": no matching files found")
}

type embeddedInputs struct {
	files, patterns, testFiles, testPatterns []string
}

func (e embeddedInputs) matches(file, dir string) (production, test bool) {
	rel, err := filepath.Rel(dir, file)
	if err != nil || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return false, false
	}
	rel = filepath.ToSlash(rel)
	production, test = slices.Contains(e.files, rel), slices.Contains(e.testFiles, rel)
	// Existing files use Go's exact resolution (including its exclusions).
	// Deleted files no longer occur in EmbedFiles; retained patterns recover
	// their owners, including a file nested beneath an embedded directory.
	if _, err := os.Stat(file); os.IsNotExist(err) {
		production = production || matchesEmbedPatterns(rel, e.patterns)
		test = test || matchesEmbedPatterns(rel, e.testPatterns)
	}
	return production, test
}

func matchesEmbedPatterns(file string, patterns []string) bool {
	for _, pattern := range patterns {
		all := strings.HasPrefix(pattern, "all:")
		pattern = strings.TrimPrefix(pattern, "all:")
		for candidate := file; candidate != "."; candidate = path.Dir(candidate) {
			match, _ := path.Match(pattern, candidate)
			if !match {
				continue
			}
			// Hidden names explicitly matched by a pattern are legal; only
			// the recursive directory walk excludes them without all:.
			if !all && candidate != file {
				hidden := false
				for _, part := range strings.Split(strings.TrimPrefix(file, candidate+"/"), "/") {
					if strings.HasPrefix(part, ".") || strings.HasPrefix(part, "_") {
						hidden = true
					}
				}
				if hidden {
					continue
				}
			}
			return true
		}
	}
	return false
}
