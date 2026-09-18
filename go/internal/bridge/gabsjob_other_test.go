//go:build !windows

package bridge

func gabsJobHelperMain([]string) bool { return false }

func gabsJobFakeChild() {}
