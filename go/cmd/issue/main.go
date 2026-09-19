// Command issue prints a GitHub issue with its comments as compact
// markdown, in one call, however long the thread is.
//
//	go run ./cmd/issue [-o <file>] [-limit <bytes>] [-last <n>] <number>...
//
// It reads the issue through `gh issue view --json`, so it works where
// `gh issue view --comments` does not: with stdout redirected that form
// prints only the comments, and nothing at all on an issue without any.
// Output is the title line, labels, the body, then every comment as
// `### <author> <date>` sections. The tool harness caps a command's
// output near 30k characters, so past -limit the remainder is cut at a
// comment boundary and the tail names the file holding the full text
// (-o, default issue-<n>.md in the scratchpad or the OS temp directory).
// -last keeps only the newest n comments, for catching up on a thread
// already read.
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

type issue struct {
	Number int    `json:"number"`
	Title  string `json:"title"`
	State  string `json:"state"`
	URL    string `json:"url"`
	Body   string `json:"body"`
	Author author `json:"author"`
	Labels []struct {
		Name string `json:"name"`
	} `json:"labels"`
	CreatedAt time.Time `json:"createdAt"`
	Comments  []comment `json:"comments"`
}

type author struct {
	Login string `json:"login"`
}

type comment struct {
	Author    author    `json:"author"`
	CreatedAt time.Time `json:"createdAt"`
	Body      string    `json:"body"`
}

const fields = "number,title,state,url,body,author,labels,createdAt,comments"

func main() {
	out := flag.String("o", "", "file to hold the full rendering (default issue-<n>.md under $CLAUDE_SCRATCHPAD or the OS temp dir)")
	limit := flag.Int("limit", 25000, "bytes to print before deferring the rest to the file")
	last := flag.Int("last", 0, "print only the newest n comments (0 = all)")
	flag.Parse()
	if flag.NArg() == 0 {
		fmt.Fprintln(os.Stderr, "usage: issue [-o file] [-limit bytes] [-last n] <number>...")
		os.Exit(2)
	}
	for _, arg := range flag.Args() {
		if err := show(arg, *out, *limit, *last); err != nil {
			fmt.Fprintf(os.Stderr, "issue %s: %v\n", arg, err)
			os.Exit(1)
		}
	}
}

func show(number, out string, limit, last int) error {
	cmd := exec.Command("gh", "issue", "view", number, "--json", fields)
	cmd.Stderr = os.Stderr
	raw, err := cmd.Output()
	if err != nil {
		return err
	}
	var is issue
	if err := json.Unmarshal(raw, &is); err != nil {
		return fmt.Errorf("decode gh output: %w", err)
	}
	full := render(is, 0)
	if out == "" {
		out = filepath.Join(scratchpad(), fmt.Sprintf("issue-%d.md", is.Number))
	}
	if err := os.WriteFile(out, full, 0o644); err != nil {
		return err
	}
	shown := full
	if last > 0 {
		shown = render(is, last)
	}
	if len(shown) <= limit {
		os.Stdout.Write(shown)
		return nil
	}
	// Cut at the last comment heading that fits, so the reader never sees
	// half a comment; the pointer tells them where the rest lives.
	cut := bytes.LastIndex(shown[:limit], []byte("\n### "))
	if cut <= 0 {
		cut = limit
	}
	os.Stdout.Write(shown[:cut])
	rest := bytes.Count(shown[cut:], []byte("\n### "))
	fmt.Printf("\n\n[... %d more comment(s), %d bytes: read %s from line %d]\n",
		rest, len(shown)-cut, out, bytes.Count(shown[:cut], []byte("\n"))+1)
	return nil
}

func render(is issue, last int) []byte {
	var b strings.Builder
	fmt.Fprintf(&b, "# #%d %s\n\n", is.Number, is.Title)
	fmt.Fprintf(&b, "%s · %s · %s · %d comments · %s\n\n",
		strings.ToLower(is.State), is.Author.Login, is.CreatedAt.Format("2006-01-02"), len(is.Comments), is.URL)
	if len(is.Labels) > 0 {
		names := make([]string, len(is.Labels))
		for i, l := range is.Labels {
			names[i] = l.Name
		}
		fmt.Fprintf(&b, "labels: %s\n\n", strings.Join(names, ", "))
	}
	comments := is.Comments
	if last > 0 && last < len(comments) {
		fmt.Fprintf(&b, "(body and %d older comment(s) omitted)\n\n", len(comments)-last)
		comments = comments[len(comments)-last:]
	} else {
		b.WriteString(strings.TrimSpace(is.Body))
		b.WriteString("\n\n")
	}
	for _, c := range comments {
		fmt.Fprintf(&b, "### %s %s\n\n%s\n\n", c.Author.Login, c.CreatedAt.Format("2006-01-02 15:04"), strings.TrimSpace(c.Body))
	}
	return []byte(strings.ReplaceAll(b.String(), "\r\n", "\n"))
}

func scratchpad() string {
	if dir := os.Getenv("CLAUDE_SCRATCHPAD"); dir != "" {
		return dir
	}
	return os.TempDir()
}
