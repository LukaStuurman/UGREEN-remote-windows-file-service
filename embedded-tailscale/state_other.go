//go:build !windows

package main

import (
	"errors"
	"tailscale.com/ipn"
)

// Production is Windows-only: never fall back to an unencrypted state file.
func openState(string) (ipn.StateStore, error) { return nil, errors.New("Windows DPAPI required") }
func lockState(string) (func(), error)         { return nil, errors.New("Windows state locking required") }
