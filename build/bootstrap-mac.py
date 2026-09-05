"""Install checksum-pinned test tools inside this checkout (Apple Silicon only)."""
import hashlib
import json
from pathlib import Path
import platform
import tarfile
import subprocess

root = Path(__file__).resolve().parent.parent
if platform.system() != "Darwin" or platform.machine() != "arm64":
    raise SystemExit("This bootstrap targets macOS arm64. Other platforms: install the SDK from global.json.")
deps = json.loads((root / "build/dependencies.json").read_text())
for key, directory, binary in [("dotnetMac", "dotnet", "dotnet"), ("powershellMac", "pwsh", "pwsh")]:
    item = deps[key]
    destination = root / ".tools" / directory
    if (destination / binary).exists():
        print(f"Already present: {destination / binary}", flush=True)
        continue
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination.parent / (directory + ".tar.gz")
    print(f"Downloading {key} {item['version']}", flush=True)
    subprocess.run(["curl", "--fail", "--location", "--retry", "4", "--retry-all-errors",
                    "--connect-timeout", "20", "--max-time", "600", "--continue-at", "-",
                    "--output", str(archive), item["url"]], check=True)
    algorithm = "sha512" if "sha512" in item else "sha256"
    with archive.open("rb") as stream:
        actual = hashlib.file_digest(stream, algorithm).hexdigest()
    if actual != item[algorithm]:
        raise SystemExit(f"Checksum mismatch: {key}; refusing to extract")
    with tarfile.open(archive) as stream:
        stream.extractall(destination, filter="data")
    (destination / binary).chmod(0o755)
    archive.unlink()
    print(f"Installed {destination / binary}", flush=True)
