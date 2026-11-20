#!/usr/bin/env python3
"""Prepare or build the KnightRay Android source overlay."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
from urllib.request import Request, urlopen
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = "https://github.com/2dust/v2rayNG.git"
RELEASE = "2.3.15"
REVISION = "57907cf354d9769200ed4717b4430d605abe0952"
AAR = ("https://github.com/2dust/AndroidLibXrayLite/releases/download/v26.10.31/libv2ray.aar",
       "2efdf731b1ed624eb1cb0d5a4d0aa95c0672e89f9702226d1ba2dc90308630b6")
APK = (f"https://github.com/2dust/v2rayNG/releases/download/{RELEASE}/v2rayNG_{RELEASE}_arm64-v8a.apk",
       "ec53590f042ebb3998169506164ca72eeb84de938d6ed8e888d3b10031f548e3")


def run(*args, cwd=None):
    subprocess.run(args, cwd=cwd, check=True)


def artifact(cache, name, origin):
    url, digest = origin
    file = cache / name
    if not file.exists():
        temp = cache / (name + ".part")
        with urlopen(Request(url, headers={"User-Agent": "KnightRay-packager"}), timeout=60) as response, temp.open("wb") as out:
            shutil.copyfileobj(response, out)
        temp.replace(file)
    if hashlib.sha256(file.read_bytes()).hexdigest() != digest:
        raise SystemExit(f"SHA-256 check failed for {file}")
    return file


def prepare(source, cache):
    if not source.exists():
        run("git", "clone", "--depth", "1", "--branch", RELEASE, UPSTREAM, source)
    if subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip() != REVISION:
        raise SystemExit(f"Unexpected Android source version at {source}")

    project = source / "V2rayNG"
    app = project / "app"
    main = app / "src/main"
    java = main / "java/com/v2ray/ang"
    overlay = ROOT / "android/overlay"

    def patch(relative, edits):
        target = project / relative
        text = subprocess.check_output(["git", "show", f"HEAD:V2rayNG/{relative}"], cwd=source, text=True)
        for before, after in edits:
            if text.count(before) != 1:
                raise SystemExit(f"Could not apply Android overlay in {relative}")
            text = text.replace(before, after, 1)
        target.write_text(text, encoding="utf-8")

    patch("app/build.gradle.kts", [
        ('applicationId = "com.v2ray.ang"', 'applicationId = "io.github.v2raytaki.android"'),
        ('    versionCode = 755\n', '    versionCode = 760\n'),
        ('    compileSdk = 37\n', '    compileSdk = 37\n    buildToolsVersion = "37.0.0"\n'),
    ])
    # Some translations override the launcher name and notification labels.
    for localized in (main / "res").glob("values*/strings.xml"):
        relative = localized.relative_to(project).as_posix()
        original = subprocess.check_output(["git", "show", f"HEAD:V2rayNG/{relative}"], cwd=source, text=True)
        localized.write_text(original.replace("v2rayNG", "KnightRay"), encoding="utf-8")
    patch("app/src/main/AndroidManifest.xml", [
        ('android:icon="@mipmap/ic_launcher"\n', 'android:icon="@drawable/taki_app_icon"\n'
         '        android:roundIcon="@drawable/taki_app_icon"\n'),
        ('android:icon="@mipmap/ic_launcher">', 'android:icon="@drawable/taki_app_icon">'),
        ('        <activity\n            android:name=".ui.main.MainActivity"',
         '        <activity android:name=".ui.fetcher.ConfigFetcherActivity" android:exported="false" />\n\n'
         '        <activity\n            android:name=".ui.main.MainActivity"'),
    ])
    ui = "app/src/main/java/com/v2ray/ang/ui/main"
    java_rel = "app/src/main/java/com/v2ray/ang"
    patch(f"{ui}/MainImportMenu.kt", [
        ('    TestAllRealPing(R.string.title_real_ping_all_server),\n',
         '    TestAllRealPing(R.string.title_real_ping_all_server),\n'
         '    TestWeb(R.string.knight_web_test),\n'),
    ])
    patch(f"{ui}/MainContract.kt", [
        ('    data object TestRealAllServers : MainAction\n',
         '    data object TestRealAllServers : MainAction\n    data object TestAllWeb : MainAction\n'),
    ])
    patch(f"{java_rel}/dto/TestServiceMessage.kt", [
        ('    val onlyTcp: Boolean = false\n', '    val onlyTcp: Boolean = false,\n    val webOnly: Boolean = false\n'),
    ])
    patch(f"{java_rel}/service/CoreTestService.kt", [
        ('                onlyTcp = message.onlyTcp,\n',
         '                onlyTcp = message.onlyTcp,\n                webOnly = message.webOnly,\n'),
        ('                    if (MmkvManager.decodeSettingsBool(AppConfig.PREF_AUTO_REMOVE_INVALID_AFTER_TEST, false)) {\n',
         '                    if (!message.webOnly && MmkvManager.decodeSettingsBool(AppConfig.PREF_AUTO_REMOVE_INVALID_AFTER_TEST, false)) {\n'),
    ])
    patch(f"{java_rel}/service/RealPingWorkerService.kt", [
        ('import com.v2ray.ang.handler.SpeedtestManager\n', 'import com.v2ray.ang.handler.SpeedtestManager\n'
         'import com.v2ray.ang.ui.fetcher.KNIGHT_WEB_TEST_URL\n'
         'import com.v2ray.ang.ui.fetcher.buildKnightWebProbe\n'),
        ('    private val onlyTcp: Boolean = false,\n',
         '    private val onlyTcp: Boolean = false,\n    private val webOnly: Boolean = false,\n'),
        ('    private suspend fun startRealPing(guid: String): Long {\n'
         '        val retFailure = -1L\n\n'
         '        val config = MmkvManager.decodeServerConfig(guid) ?: return retFailure\n',
         '    private suspend fun startRealPing(guid: String): Long {\n'
         '        val retFailure = -1L\n\n'
         '        val config = MmkvManager.decodeServerConfig(guid) ?: return retFailure\n'
         '        if (webOnly) {\n'
         '            val probe = buildKnightWebProbe(config) ?: return 0L // Unsupported profiles remain untested.\n'
         '            return CoreNativeManager.measureOutboundDelay(probe, KNIGHT_WEB_TEST_URL)\n'
         '        }\n'),
    ])
    patch(f"{ui}/MainDrawer.kt", [
        ('R.mipmap.ic_launcher_foreground', 'R.drawable.taki_app_icon'),
        ('import androidx.compose.ui.graphics.BlendMode\n'
         'import androidx.compose.ui.graphics.Color\n'
         'import androidx.compose.ui.graphics.ColorFilter\n', ''),
        ('import com.v2ray.ang.ui.compose.LocalDarkTheme\n', ''),
        ('                    val isDarkTheme = LocalDarkTheme.current\n', ''),
        ('                        modifier = Modifier.size(120.dp),\n'
         '                        colorFilter = if (isDarkTheme) {\n'
         '                            ColorFilter.tint(Color.White, BlendMode.SrcIn)\n'
         '                        } else {\n'
         '                            null\n'
         '                        }\n', '                        modifier = Modifier.size(120.dp)\n'),
        ('    Settings(R.drawable.ic_settings_24dp', '    ConfigFetcher(R.drawable.ic_settings_24dp, R.string.taki_fetch_title),\n'
         '    Settings(R.drawable.ic_settings_24dp'),
        ('    MainDestination.Settings\n', '    MainDestination.ConfigFetcher,\n    MainDestination.Settings\n'),
        ('    Promotion(R.drawable.ic_promotion_24dp, R.string.title_pref_promotion),\n', ''),
        ('    MainDestination.Promotion,\n', ''),
        ('    MainDestination.CheckUpdate,\n', ''),
    ])
    patch(f"{ui}/MainActivity.kt", [
        ('            MainDestination.Settings ->', '            MainDestination.ConfigFetcher -> Intent(this, com.v2ray.ang.ui.fetcher.ConfigFetcherActivity::class.java)\n'
         '            MainDestination.Settings ->'),
        ('            MainDestination.Promotion -> {\n'
         '                Utils.openUri(\n'
         '                    this,\n'
         '                    "${Utils.decode(AppConfig.APP_PROMOTION_URL)}?t=${System.currentTimeMillis()}"\n'
         '                )\n'
         '                return\n'
         '            }\n', ''),
    ])
    patch(f"{java_rel}/AppConfig.kt", [
        ('    const val PREF_SNIFFING_ENABLED', '    const val PREF_BYPASS_IRAN_IPS = "knight_bypass_iran_ips"\n'
         '    const val DEFAULT_BYPASS_IRAN_IPS = true\n'
         '    const val PREF_SNIFFING_ENABLED'),
    ])
    patch(f"{java_rel}/ui/settings/SettingsActivity.kt", [
        ('    var localDns by rememberMmkvBool', '    var bypassIranIps by rememberMmkvBool(AppConfig.PREF_BYPASS_IRAN_IPS, AppConfig.DEFAULT_BYPASS_IRAN_IPS)\n'
         '    var localDns by rememberMmkvBool'),
        ('            if (coreSettingsExpanded) {\n', '            if (coreSettingsExpanded) {\n'
         '                SettingsSwitchItem(\n'
         '                    title = stringResource(R.string.knight_bypass_iran_ips),\n'
         '                    summary = stringResource(R.string.knight_bypass_iran_ips_summary),\n'
         '                    checked = bypassIranIps,\n'
         '                    onCheckedChange = { bypassIranIps = it }\n'
         '                )\n'),
    ])
    patch(f"{java_rel}/core/CoreConfigManager.kt", [
        ('            appendRoutingUserRule(configContext, key, v2rayConfig, policyGroupBalancerTags)\n'
         '        }\n',
         '            appendRoutingUserRule(configContext, key, v2rayConfig, policyGroupBalancerTags)\n'
         '        }\n'
         '        if (MmkvManager.decodeSettingsBool(AppConfig.PREF_BYPASS_IRAN_IPS, AppConfig.DEFAULT_BYPASS_IRAN_IPS)) {\n'
         '            val rules = v2rayConfig.routing.rules\n'
         '            // Keep leading block rules ahead of the bypass, and the bypass ahead of catch-alls.\n'
         '            val index = rules.indexOfFirst { it.outboundTag != AppConfig.TAG_BLOCKED }\n'
         '            rules.add(if (index < 0) rules.size else index, V2rayConfig.RoutingBean.RulesBean(\n'
         '                ip = listOf("geoip:ir"), outboundTag = AppConfig.TAG_DIRECT\n'
         '            ))\n'
         '        }\n'),
    ])
    patch(f"{ui}/MainViewModel.kt", [
        ('import androidx.lifecycle.viewModelScope\n', 'import androidx.lifecycle.viewModelScope\n'
         'import com.v2ray.ang.core.LauncherManager\n'
         'import com.v2ray.ang.ui.fetcher.AutoSelectSession\n'
         'import com.v2ray.ang.ui.fetcher.sortByLowestDelay\n'),
        ('    val uiState: StateFlow<MainUiState> = _uiState.asStateFlow()\n',
         '    val uiState: StateFlow<MainUiState> = _uiState.asStateFlow()\n'
         '    private val delaySortedGroups = ConcurrentHashMap.newKeySet<String>()\n'
         '    internal val autoSelect = AutoSelectSession(\n'
         '        viewModelScope, dataSource::getSelectServer,\n'
         '        activate = { guid ->\n'
         '            updateSelectedGuid(guid)\n'
         '            LauncherManager.restartService(application)\n'
         '        },\n'
         '        publishDelays = { groupId, updates ->\n'
         '            if (testRequests.bulk == null) {\n'
         '                delaySortedGroups.add(groupId)\n'
         '                val servers = cacheMutex.withLock {\n'
         '                    val old = groupDataCache[groupId]\n'
         '                    if (old != null) applyTestDelayResults(old, updates).also { groupDataCache[groupId] = it }\n'
         '                    else applyTestDelayResults(mutableServerGroupState(groupId).value.servers, updates)\n'
         '                }\n'
         '                updateGroupUi(groupId, servers)\n'
         '            }\n'
         '        },\n'
         '    )\n'),
        ('            MainAction.TestAllServers -> testAllRealPing(true)\n',
         '            MainAction.TestAllServers -> testAllRealPing(true)\n'
         '            MainAction.TestAllWeb -> testAllRealPing(webOnly = true)\n'),
        ('        val filteredServers = applyKeywordFilter(servers)\n',
         '        val filtered = applyKeywordFilter(servers)\n'
         '        val filteredServers = if (groupId in delaySortedGroups) sortByLowestDelay(filtered) { it.testDelayMillis } else filtered\n'),
        ('        val guids = servers.map { it.guid }\n        mutableServerGroupState(groupId).value = ServerGroupUiState(servers, rows)',
         '        val guids = servers.map { it.guid }\n'
         '        delaySortedGroups.remove(groupId)\n'
         '        mutableServerGroupState(groupId).value = ServerGroupUiState(servers, rows)'),
        ('    fun testAllRealPing(onlyTcp: Boolean = false) {\n',
         '    fun testAllRealPing(onlyTcp: Boolean = false, webOnly: Boolean = false) {\n'
         '        autoSelect.stop()\n'),
        ('            onlyTcp = onlyTcp\n', '            onlyTcp = onlyTcp,\n            webOnly = webOnly\n'),
        ('        if (testRequests.completeBulk(requestId) == null) return\n        resetTestStatus()\n',
         '        val completed = testRequests.completeBulk(requestId) ?: return\n'
         '        delaySortedGroups.add(completed.groupId)\n'
         '        updateGroupUi(completed.groupId, mutableServerGroupState(completed.groupId).value.servers)\n'
         '        resetTestStatus()\n'),
    ])
    patch(f"{ui}/MainScreen.kt", [
        ('import com.v2ray.ang.ui.compose.QRCodeDialog\n', 'import com.v2ray.ang.ui.compose.QRCodeDialog\n'
         'import com.v2ray.ang.ui.fetcher.AutoSelectControls\n'),
        ('    val groups = uiState.groups\n', '    val groups = uiState.groups\n'
         '    val autoServers by mainViewModel.serversForGroup(uiState.selectedGroupId).collectAsStateWithLifecycle()\n'),
        ('                    HorizontalPager(\n',
         '                    AutoSelectControls(mainViewModel.autoSelect, uiState.selectedGroupId, autoServers, !uiState.isTesting)\n\n'
         '                    HorizontalPager(\n'),
        ('                            MainMoreMenuAction.TestAllRealPing -> onAction(MainAction.TestRealAllServers)\n',
         '                            MainMoreMenuAction.TestAllRealPing -> onAction(MainAction.TestRealAllServers)\n'
         '                            MainMoreMenuAction.TestWeb -> onAction(MainAction.TestAllWeb)\n'),
    ])
    patch("app/src/main/java/com/v2ray/ang/handler/AngConfigManager.kt", [
        ('    private fun parseConfig(\n', '    internal fun parseFetcherProfile(link: String): ProfileItem? = parseConfig(link, "", null)\n\n'
         '    private fun parseConfig(\n'),
    ])
    google_mirror = os.getenv("TAKI_ANDROID_GOOGLE_MAVEN")
    if google_mirror:
        if not google_mirror.startswith("https://") or '"' in google_mirror or '\n' in google_mirror:
            raise SystemExit("Expected an HTTPS Google Maven mirror")
        patch("settings.gradle.kts", [
            ('        google {\n', f'        maven("{google_mirror}") {{\n'),
            ('        google()\n', f'        maven("{google_mirror}") {{\n'
             '            content {\n'
             '                includeGroupByRegex("com\\\\.android.*")\n'
             '                includeGroupByRegex("androidx.*")\n'
             '            }\n'
             '        }\n'),
        ])
    else:
        patch("settings.gradle.kts", [])

    ui_path = java / "ui/fetcher"
    ui_path.mkdir(parents=True, exist_ok=True)
    for file in overlay.glob("*.kt"):
        shutil.copyfile(file, ui_path / file.name)
    tests = app / "src/test/java/com/v2ray/ang/ui/fetcher"
    tests.mkdir(parents=True, exist_ok=True)
    for file in (ROOT / "android/tests").glob("*.kt"):
        shutil.copyfile(file, tests / file.name)
    shutil.copyfile(overlay / "taki_fetcher_sources.json", main / "assets/taki_fetcher_sources.json")
    shutil.copyfile(overlay / "taki_fetcher_strings.xml", main / "res/values/taki_fetcher_strings.xml")
    icon_dir = main / "res/drawable-nodpi"
    icon_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(ROOT / "v2rayN/v2rayN.Desktop/Assets/v2rayTaki.png", icon_dir / "taki_app_icon.png")

    cache.mkdir(parents=True, exist_ok=True)
    libs = app / "libs"
    native = libs / "arm64-v8a"
    native.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(artifact(cache, "libv2ray.aar", AAR), libs / "libv2ray.aar")
    with ZipFile(artifact(cache, "upstream.apk", APK)) as release:
        for name in ("libhev-socks5-tunnel.so", "libhevsockstun.so"):
            (native / name).write_bytes(release.read(f"lib/arm64-v8a/{name}"))
        for name in ("geoip.dat", "geosite.dat", "geoip-only-cn-private.dat"):
            (main / "assets" / name).write_bytes(release.read(f"assets/{name}"))
    return project


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, default=ROOT / "dist/.android-work")
    parser.add_argument("--prepare", action="store_true")
    options = parser.parse_args()
    work = options.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    cache = Path(os.environ.get("TAKI_ANDROID_ASSET_CACHE", work / "assets")).resolve()
    project = prepare(work / "upstream", cache)
    if options.prepare:
        print(f"Android Studio project: {project}")
        return
    gradle = os.environ.get("TAKI_ANDROID_GRADLE", str(project / ("gradlew.bat" if os.name == "nt" else "gradlew")))
    run(gradle, ":app:assemblePlaystoreDebug", "-PABI_FILTERS=arm64-v8a", cwd=project)
    artifacts = list((project / "app/build/outputs/apk/playstore/debug").glob("*arm64-v8a*.apk"))
    if len(artifacts) != 1:
        raise SystemExit("Could not locate one arm64-v8a APK")
    destination = ROOT / "dist/KnightRay.apk"
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(artifacts[0], destination)
    for old_name in ("KnightRay-android-arm64-debug.apk", "v2rayTaki-android-arm64-debug.apk"):
        destination.with_name(old_name).unlink(missing_ok=True)
    print(f"Created {destination}")


if __name__ == "__main__":
    main()
