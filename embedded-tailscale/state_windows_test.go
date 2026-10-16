package main

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

func TestPersistentIdentityUsesDPAPIAndFailsClosedOnCorruption(t *testing.T) {
	if os.Getenv("GITHUB_ACTIONS") != "true" {
		t.Skip("Native encrypted storage fixture is limited to a disposable runner")
	}
	path := filepath.Join(t.TempDir(), "identity.dpapi")
	store, err := openState(path)
	if err != nil {
		t.Fatal(err)
	}
	secret := bytes.Repeat([]byte("fixture-node-key"), 16)
	if err = store.WriteState("fixture-key", secret); err != nil {
		t.Fatal(err)
	}
	encrypted, err := os.ReadFile(path)
	if err != nil || bytes.Contains(encrypted, secret) {
		t.Fatal("Plaintext identity persisted", err)
	}
	reopened, err := openState(path)
	if err != nil {
		t.Fatal(err)
	}
	actual, err := reopened.ReadState("fixture-key")
	if err != nil || !bytes.Equal(actual, secret) {
		t.Fatal("Identity did not survive restart", err)
	}
	if err = reopened.WriteState("fixture-key", nil); err != nil {
		t.Fatal(err)
	}
	if _, err = reopened.ReadState("fixture-key"); err == nil {
		t.Fatal("Deleted identity survived")
	}
	if err = os.WriteFile(path, []byte("corrupt fixture"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = openState(path); err == nil {
		t.Fatal("Corrupt state silently replaced")
	}
}
