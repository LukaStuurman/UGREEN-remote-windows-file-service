"""Prepare a pinned private module copy; never edit Go's shared module cache."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parent
module = json.loads(subprocess.check_output(['go', 'list', '-m', '-json', 'tailscale.com'], text=True))
if module['Version'] != 'v1.104.1':
    raise RuntimeError('Re-review the private adapter patch for the new Tailscale version')
source_root = Path(module['Dir']).resolve()
source = source_root / 'net/tstun/tun_windows.go'
if hashlib.sha256(source.read_bytes()).hexdigest() != 'd0676e4e51c168807dac7f03ca0a433beb87aa52f565b2490c475ecf0447384b':
    raise RuntimeError('Pinned upstream Windows adapter source has changed')

# The caller creates a new temporary directory inside ignored build resources.
destination = Path(sys.argv[1]).resolve()
if destination.exists():
    raise RuntimeError('Private module destination must be new')
shutil.copytree(source_root, destination, copy_function=shutil.copyfile)
shutil.copyfile(root / 'tun_windows.go', destination / 'net/tstun/tun_windows.go')
shutil.copyfile(root / 'private_guid_windows_test.go', destination / 'net/tstun/techbase_private_guid_windows_test.go')
print('Verified and prepared private Windows adapter source from Tailscale v1.104.1.')
