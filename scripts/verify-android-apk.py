#!/usr/bin/env python3
"""Check the packaged KnightRay APK against the shipping requirements."""
import argparse
import os
import re
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_ABIS = {"arm64-v8a", "armeabi-v7a"}
SHIELD = "drawable/ic_knight_shield"


def sdk_tool(name):
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or str(Path.home() / "android-sdk")
    builds = sorted(Path(sdk, "build-tools").glob("*"), key=lambda p: [int(x) for x in re.findall(r"\d+", p.name)])
    if not builds:
        raise SystemExit(f"No build-tools directory under {sdk}")
    return str(builds[-1] / name)


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def check(condition, message, failures):
    print(("ok   " if condition else "FAIL ") + message)
    if not condition:
        failures.append(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", nargs="?", type=Path, default=ROOT / "dist/KnightRay.apk")
    parser.add_argument("--max-mb", type=float, default=45.0)
    options = parser.parse_args()
    apk = options.apk.resolve()
    failures = []

    if not apk.is_file():
        raise SystemExit(f"Missing APK: {apk}")
    size_mb = apk.stat().st_size / (1024 * 1024)

    badging = run(sdk_tool("aapt2"), "dump", "badging", str(apk))
    min_sdk = int(re.search(r"minSdkVersion:'(\d+)'", badging).group(1))
    native = set(re.search(r"native-code: (.+)", badging).group(1).replace("'", "").split())
    tree = run(sdk_tool("aapt2"), "dump", "xmltree", "--file", "AndroidManifest.xml", str(apk))
    resources = run(sdk_tool("aapt2"), "dump", "resources", str(apk))

    check(size_mb < options.max_mb, f"APK is {size_mb:.1f} MiB (budget {options.max_mb:.0f} MiB)", failures)
    check(min_sdk == 24, f"minSdkVersion is {min_sdk}", failures)
    check(native == EXPECTED_ABIS, f"native ABIs are {sorted(native)}", failures)
    check("android:debuggable" not in tree, "release build is not debuggable", failures)

    anchor = tree.find('"com.v2ray.ang.service.QSTileService"')
    tile = tree[max(0, anchor - 600):anchor] if anchor >= 0 else ""
    icon_ref = re.search(r"icon\(0x01010002\)=@(0x[0-9a-f]+)", tile)
    icon_name = None
    if icon_ref:
        match = re.search(rf"resource {icon_ref.group(1)} (\S+)", resources)
        icon_name = match.group(1) if match else None
    check(icon_name == SHIELD, f"Quick Settings tile icon is {icon_name}", failures)
    check(
        bool(re.search(rf"resource 0x[0-9a-f]+ {re.escape(SHIELD)}\n\s+\([^)]*\) \(file\) \S+ type=XML", resources)),
        "shield tile icon is a vector drawable",
        failures,
    )
    with zipfile.ZipFile(apk) as archive:
        dex = b"".join(archive.read(n) for n in archive.namelist() if n.endswith(".dex"))
    for symbol in (b"com/v2ray/ang/core/LauncherManager", b"com/v2ray/ang/service/QSTileService", b"com/tencent/mmkv/MMKV"):
        check(symbol in dex, f"{symbol.decode()} survived R8", failures)

    try:
        run(sdk_tool("apksigner"), "verify", str(apk))
        signed = True
    except subprocess.CalledProcessError:
        signed = False
    check(signed, "APK signature verifies", failures)

    if failures:
        print(f"\n{len(failures)} check(s) failed")
        return 1
    print(f"\nAll checks passed ({size_mb:.1f} MiB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
