"""Collect the license texts of modules actually imported by the Windows helper."""
from pathlib import Path
import subprocess
import argparse

parser = argparse.ArgumentParser()
parser.add_argument('--output', default='THIRD-PARTY-NOTICES.txt')
parser.add_argument('targets', nargs='*', default=['.'])
args = parser.parse_args()

lines = subprocess.check_output(
    ["go", "list", "-deps", "-f", "{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}", *args.targets],
    text=True, encoding="utf-8",
).splitlines()
modules = sorted({tuple(line.split("|", 2)) for line in lines if line.count("|") == 2})
chunks = ["UGREEN Remote Drive embedded Tailscale transport\nThird-party license notices\n"]
go_root = Path(subprocess.check_output(["go", "env", "GOROOT"], text=True).strip())
chunks.append("Go standard library\n" + (go_root / "LICENSE").read_text(encoding="utf-8"))
for module, version, directory in modules:
    if module.startswith("ugreen-remote-drive/"):
        continue
    root = Path(directory)
    files = sorted({p for pattern in ("LICENSE*", "LICENCE*", "COPYING*", "NOTICE*", "COPYRIGHT*") for p in root.glob(pattern) if p.is_file()})
    if not files:
        raise RuntimeError(f"Missing license text for {module} {version}")
    chunks.append(f"\n{'=' * 72}\n{module} {version}\n")
    for file in files:
        chunks.append(f"\n--- {file.name} ---\n" + file.read_text(encoding="utf-8", errors="replace"))
Path(args.output).write_text("\n".join(chunks), encoding="utf-8")
print(f"Collected notices for {len(modules) - 1} imported modules.")
