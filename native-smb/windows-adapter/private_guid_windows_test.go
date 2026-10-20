package tstun

import "testing"

func TestTechbasePrivateGUIDIsolation(t *testing.T) {
	stock, err := techbaseAdapterGUID("Tailscale")
	if err != nil {
		t.Fatal(err)
	}
	first, err := techbaseAdapterGUID("UGREENPortablefixtureA")
	if err != nil {
		t.Fatal(err)
	}
	same, _ := techbaseAdapterGUID("UGREENPortablefixtureA")
	second, _ := techbaseAdapterGUID("UGREENPortablefixtureB")
	if first != same || first == second || first == stock || second == stock {
		t.Fatal("private adapters must be stable, distinct per name and separate from the system adapter")
	}
}
