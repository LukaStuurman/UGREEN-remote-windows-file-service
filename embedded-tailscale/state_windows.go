package main

import (
	"encoding/json"
	"errors"
	"golang.org/x/sys/windows"
	"os"
	"path/filepath"
	"sync"
	"tailscale.com/ipn"
	"unsafe"
)

type protectedState struct {
	mu     sync.Mutex
	path   string
	values map[ipn.StateKey][]byte
}

func protect(data []byte, decrypt bool) ([]byte, error) {
	if len(data) == 0 {
		return nil, errors.New("empty-state")
	}
	in := windows.DataBlob{Size: uint32(len(data)), Data: &data[0]}
	var out windows.DataBlob
	var err error
	if decrypt {
		err = windows.CryptUnprotectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out)
	} else {
		err = windows.CryptProtectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out)
	}
	if err != nil {
		return nil, err
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	buffer := unsafe.Slice(out.Data, out.Size)
	defer clear(buffer)
	return append([]byte(nil), buffer...), nil
}
func openState(path string) (*protectedState, error) {
	s := &protectedState{path: path, values: make(map[ipn.StateKey][]byte)}
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return s, nil
	}
	if err != nil {
		return nil, err
	}
	plain, err := protect(data, true)
	if err != nil {
		return nil, err
	}
	defer clear(plain)
	if err = json.Unmarshal(plain, &s.values); err != nil {
		return nil, err
	}
	return s, nil
}
func (s *protectedState) ReadState(key ipn.StateKey) ([]byte, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	value, ok := s.values[key]
	if !ok {
		return nil, ipn.ErrStateNotExist
	}
	return append([]byte(nil), value...), nil
}
func (s *protectedState) WriteState(key ipn.StateKey, value []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	next := make(map[ipn.StateKey][]byte, len(s.values)+1)
	for k, v := range s.values {
		next[k] = v
	}
	if value == nil {
		delete(next, key)
	} else {
		next[key] = append([]byte(nil), value...)
	}
	plain, err := json.Marshal(next)
	if err != nil {
		return err
	}
	defer clear(plain)
	data, err := protect(plain, false)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(filepath.Dir(s.path), 0700); err != nil {
		return err
	}
	f, err := os.CreateTemp(filepath.Dir(s.path), "identity-*.tmp")
	if err != nil {
		return err
	}
	name := f.Name()
	defer os.Remove(name)
	if _, err = f.Write(data); err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	if err = os.Rename(name, s.path); err != nil {
		return err
	}
	s.values = next
	return nil
}
