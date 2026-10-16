package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestPinnedSMBOriginalSourcesUnmodified(t *testing.T) {
	root := filepath.Join("third_party", "go-smb2")
	manifest, err := os.ReadFile(filepath.Join(root, "UPSTREAM-SHA256.json"))
	if err != nil {
		t.Fatal(err)
	}
	var files map[string]string
	if err = json.Unmarshal(manifest, &files); err != nil {
		t.Fatal(err)
	}
	if len(files) < 40 {
		t.Fatal("Incomplete pinned upstream manifest")
	}
	for name, want := range files {
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(name)))
		if err != nil {
			t.Fatal(err)
		}
		sum := sha256.Sum256(data)
		if hex.EncodeToString(sum[:]) != want {
			t.Fatalf("Pinned upstream changed: %s", name)
		}
	}
}
