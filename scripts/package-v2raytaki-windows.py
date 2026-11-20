#!/usr/bin/env python3
"""Build a self-contained KnightRay ZIP for Windows x64."""

import hashlib
import os
import shutil
import subprocess
from pathlib import Path, PurePosixPath
from tempfile import TemporaryDirectory
from urllib.request import Request, urlopen
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
NAME = "KnightRay-windows-x64"
CORES = (
    (
        "https://github.com/XTLS/Xray-core/releases/download/v26.11.9/Xray-windows-64.zip",
        "1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d",
        {
            "xray.exe": "xray/xray.exe",
            "wintun.dll": "xray/wintun.dll",
            "geoip.dat": "geoip.dat",
            "geosite.dat": "geosite.dat",
            "LICENSE": "xray/LICENSE.txt",
            "LICENSE-Wintun": "xray/LICENSE-Wintun.txt",
        },
    ),
    (
        "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip",
        "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df",
        {
            "sing-box.exe": "sing_box/sing-box.exe",
            "libcronet.dll": "sing_box/libcronet.dll",
            "LICENSE": "sing_box/LICENSE.txt",
        },
    ),
)


def assert_windows_x64(executable):
    with executable.open("rb") as stream:
        if stream.read(2) == b"MZ":
            stream.seek(0x3C)
            stream.seek(int.from_bytes(stream.read(4), "little"))
            if stream.read(6) == b"PE\0\0\x64\x86":
                return
    raise ValueError(f"Not a Windows x64 executable: {executable}")


def add_core(scratch, destination, url, expected, files):
    filename = url.rsplit("/", 1)[1]
    cache = os.environ.get("V2RAYTAKI_CORE_CACHE")
    cached = Path(cache) / filename if cache else None
    archive = cached if cached and cached.is_file() else scratch / filename
    if not archive.is_file():
        request = Request(url, headers={"User-Agent": "KnightRay-packager"})
        with urlopen(request, timeout=45) as response, archive.open("wb") as output:
            shutil.copyfileobj(response, output)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise ValueError(f"SHA-256 mismatch: {filename}")
    with ZipFile(archive) as release:
        for name, relative_path in files.items():
            matches = [item for item in release.infolist()
                       if not item.is_dir() and PurePosixPath(item.filename).name == name]
            if len(matches) != 1:
                raise ValueError(f"Expected one {name} in {filename}; found {len(matches)}")
            path = destination / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            with release.open(matches[0]) as source, path.open("wb") as output:
                shutil.copyfileobj(source, output)
            if path.suffix.lower() in (".exe", ".dll"):
                assert_windows_x64(path)


def main():
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="windows-", dir=dist) as temp:
        scratch = Path(temp)
        bundle = scratch / NAME
        subprocess.run([
            os.environ.get("DOTNET", "dotnet"), "publish",
            ROOT / "v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj",
            "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:TakiWindowsBuild=true", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
            "-o", bundle,
        ], cwd=ROOT, check=True)
        assert_windows_x64(bundle / "KnightRay.exe")
        assert_windows_x64(bundle / "coreclr.dll")
        for core in CORES:
            add_core(scratch, bundle / "bin", *core)
        shutil.copyfile(ROOT / "LICENSE", bundle / "LICENSE.txt")
        (bundle / "README.txt").write_text(
            "KnightRay for Windows x64\n\n"
            "Extract this entire ZIP and open KnightRay.exe. Keep the other files beside it.\n"
            "Open Tools > Config Fetcher & Tester and click Fetch & Test to use all enabled repositories.\n"
            "To test one repository, switch the scope to Selected repository.\n"
            "Select one entry in Working and click Share QR code to share it before importing.\n"
            "Select passing entries in Working and click Import selected to save them.\n"
            "Click Start Auto Select above the main list to check all listed configs and use the fastest working one.\n"
            "Your settings and cores are stored separately in %LOCALAPPDATA%\\v2rayTaki.\n"
            "The app leaves Windows system proxy settings unchanged by default.\n",
            encoding="utf-8",
        )
        archive = shutil.make_archive(str(scratch / NAME), "zip", scratch, NAME)
        destination = dist / f"{NAME}.zip"
        Path(archive).replace(destination)
    print(f"Created {destination}")


if __name__ == "__main__":
    main()
