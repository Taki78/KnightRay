#!/usr/bin/env python3
"""Install pinned upstream proxy cores into a KnightRay Linux bundle."""

import hashlib
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
from tarfile import open as tar_open
from urllib.request import Request, urlopen
from zipfile import ZipFile


SPECS = {
    "linux-x64": (
        ("Xray-linux-64.zip", "b651f7e1d9966ee2c1b1ee1bf8b52c72771824d3065dce96bbc8bcc19ed95f82"),
        ("sing-box-1.14.4-linux-amd64.tar.gz", "699a1b510aa4396b2176d32d1181920399ebf1e267835da6f3b598d0f7c24968"),
    ),
    "linux-arm64": (
        ("Xray-linux-arm64-v8a.zip", "64ed6858982a7f5eb85d88b811089063bd4f17561f4c8f44e0f1dc8e6f6430be"),
        ("sing-box-1.14.4-linux-arm64.tar.gz", "436441f76ee2f7fe21b5b2d426d3cb261a480455a704865789ad533e4273ec26"),
    ),
}


def fetch(cache, repo, tag, spec):
    name, expected = spec
    archive = cache / name
    if not archive.is_file():
        url = f"https://github.com/{repo}/releases/download/{tag}/{name}"
        temporary = archive.with_suffix(".part")
        with urlopen(Request(url, headers={"User-Agent": "KnightRay-packager"}), timeout=60) as source:
            with temporary.open("wb") as target:
                shutil.copyfileobj(source, target)
        if hashlib.sha256(temporary.read_bytes()).hexdigest() != expected:
            raise SystemExit(f"SHA-256 mismatch: {name}")
        temporary.replace(archive)
    elif hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise SystemExit(f"SHA-256 mismatch: {name}")
    return archive


def copy_entry(opener, names, source, destination, executable=False):
    matches = [entry for entry in names if PurePosixPath(entry).name == source]
    if len(matches) != 1:
        raise SystemExit(f"Expected one core asset named {source}; got {len(matches)}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with opener(matches[0]) as content, destination.open("wb") as output:
        shutil.copyfileobj(content, output)
    destination.chmod(0o755 if executable else 0o644)


def main(rid, bin_dir, work):
    xray_spec, sing_spec = SPECS[rid]
    cache = Path(os.environ.get("KNIGHTRAY_CORE_CACHE", work))
    cache.mkdir(parents=True, exist_ok=True)
    xray = fetch(cache, "XTLS/Xray-core", "v26.11.9", xray_spec)
    sing = fetch(cache, "SagerNet/sing-box", "v1.14.4", sing_spec)
    with ZipFile(xray) as archive:
        names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
        for source, destination in (
            ("xray", "xray/xray"), ("LICENSE", "xray/LICENSE.txt"),
            ("geoip.dat", "geoip.dat"), ("geosite.dat", "geosite.dat"),
        ):
            copy_entry(archive.open, names, source, bin_dir / destination, executable=source == "xray")
    with tar_open(sing, "r:gz") as archive:
        names = [entry.name for entry in archive.getmembers() if entry.isfile()]
        copy_entry(archive.extractfile, names, "sing-box", bin_dir / "sing_box/sing-box", executable=True)
        copy_entry(archive.extractfile, names, "LICENSE", bin_dir / "sing_box/LICENSE.txt")
        for name in names:
            base = PurePosixPath(name).name
            if base.startswith("libcronet.so"):
                copy_entry(archive.extractfile, names, base, bin_dir / "sing_box" / base)


if __name__ == "__main__":
    main(sys.argv[1], Path(sys.argv[2]), sys.argv[3])
