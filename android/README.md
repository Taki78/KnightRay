# KnightRay for Android

The Android port builds on v2rayNG 2.3.15. Its application ID is
`io.github.v2raytaki.android`, so it upgrades v2rayTaki when signed with the same key
and installs separately from v2rayNG. The build supports ARM64 and 32-bit ARM
devices (Android 7.0 or later). The shipped APK carries the ARM64 and 32-bit ARM
libraries, which covers phones and tablets; the emulator-only x86 libraries are
stripped from the package.

Open the navigation menu and select **Config Fetcher & Tester**. Choose a
repository, adjust **Entries per file** if needed (the default is 35), and tap **Fetch & Test**. You can also
fetch all enabled repositories (the default) or paste share links. The entry limit applies
to each downloaded file. **Restore defaults** adds
back deleted built-ins and preserves your existing repositories and edits.
Tap **Share QR code** beside a passing result to share it before importing.
Select passing results and tap **Import selected**. They appear under
**Fetched Configs** on the main screen. Select a saved config and use
KnightRay's Connect button to start Android's VPN permission flow.

Tap **Start Auto Select** above the main config list to repeatedly test the
current list and pick the working config with the lowest HTTPS delay. It
waits 300 seconds after each round by default. Tap **Settings** beside the
button to shorten the wait or choose a subset. If the VPN is already running,
KnightRay switches it when the winner changes. Connect manually to start a
stopped VPN. Android can stop checks if it kills the app process.

The main list sorts by lowest measured delay after a batch test or Auto Select
round. Failed and untested entries are shown at the end. Open **⋮ → Web test
(HTTPS)** to send an HTTPS request through each supported profile in the current
list, without connecting the VPN or deleting any profiles. The fetcher's
working results also show the lowest delay first.

This port uses the Android Xray library. It does not run the desktop binaries.
The repository defaults are the same seven as on desktop; links that the
Android Xray client cannot parse or test are listed separately.

To build from the repository root, use Python 3, JDK 21, and the Android SDK.
Set `JAVA_HOME` and `ANDROID_HOME` to their installed locations. Install the
Android 37 platform and Build Tools 36.0.0 in Android Studio's SDK Manager,
then run:

```sh
python3 scripts/package-v2raytaki-android.py
```

The build produces one file, `dist/KnightRay.apk`, a release APK for direct
installation on supported Android devices. It is R8-minified with resource
shrinking and signed with the debug key so it keeps the upgrade path of earlier
KnightRay builds. Run `python3 scripts/verify-android-apk.py` after building to
check the ABI set, size budget, tile icon and signature. The script
prepares a pinned upstream checkout in `dist/.android-work/upstream` and
verifies the checksums of the Android Xray library and the upstream APK
from which it copies the matching HEV libraries and geo assets. This
workspace retains the overlay source; the upstream GPL license is part of
the prepared checkout. For source preparation without building, pass
`--prepare` and open the printed directory in Android Studio.
