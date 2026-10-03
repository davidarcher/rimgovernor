package main

import "fmt"

// syncMain fetches origin/main and fast-forwards local main to it, so a
// landing never builds on a main that is behind what others have pushed.
// A main checkout (already verified clean) is moved with it. A local main
// holding unpushed landings that origin/main lacks is refused: the caller
// pushes or rebases them first.
func syncMain(worktree, mainCheckout string) error {
	if _, err := git(worktree, "fetch", "origin", "main"); err != nil {
		fmt.Printf("origin/main not fetched (%v); landing on local main as it is\n", err)
		return nil
	}
	if _, err := git(worktree, "merge-base", "--is-ancestor", "origin/main", "main"); err == nil {
		return nil // main already contains origin/main
	}
	if _, err := git(worktree, "merge-base", "--is-ancestor", "main", "origin/main"); err != nil {
		return fmt.Errorf("local main and origin/main have diverged; push or rebase local main onto origin/main (git rebase origin/main main), then run land again")
	}
	if mainCheckout != "" {
		if _, err := git(mainCheckout, "merge", "--ff-only", "origin/main"); err != nil {
			return fmt.Errorf("fast-forwarding the main checkout %s to origin/main: %w", mainCheckout, err)
		}
	} else if _, err := git(worktree, "update-ref", "-m", "land: fast-forward to origin/main", "refs/heads/main", "origin/main"); err != nil {
		return err
	}
	fmt.Println("main fast-forwarded to origin/main")
	return nil
}
