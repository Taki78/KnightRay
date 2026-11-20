# v2rayTaki: Config Fetcher & Tester

## Goal

Fork [2dust/v2rayN](https://github.com/2dust/v2rayN) and add a **Config Fetcher & Tester** screen to its Linux desktop app. In one run, fetch proxy configs from sources the user adds, reject invalid entries, remove duplicates, and check whether each remaining config can actually carry traffic. Let the user review and import only the working configs.

Implemented locally as **v2rayTaki**. The Linux app, settings, and installer have their own identity so they can coexist with upstream v2rayN. The working copy tracks upstream; a hosted fork can be published once a destination account is chosen.

## User flow

1. Open **Tools → Config Fetcher & Tester**. Choose which repository sources to search, add a new repository or HTTPS subscription URL, or paste proxy links. For the first version, support plain lists and Base64-encoded lists of upstream-supported share links. Report unsupported input formats separately.
2. Click **Fetch & Test**. Test the selected repository by default, with explicit options for all enabled repositories and pasted links only. Show progress and display results as tests finish. Allow cancellation and retesting of failed or untested entries without another fetch.
3. Show the display name, protocol, source, status, and latency when available. Keep rejected entries in a separate results view with a short reason. Never display raw credentials or subscription tokens in diagnostics.
4. Click **Import Working** to add the selected passing configs to a dedicated **Fetched Configs** group. Exclude duplicates again at import time so repeating the same run is safe. Leave existing profiles and the active connection alone.

## Repository sources

Add a **Manage Sources** view. Users can add a repository by pasting an HTTPS file URL or entering an HTTPS URL template and the file keys it should expand to. Each saved source has a display name, a URL or template, optional file keys, and an enabled toggle. The fetcher starts with 10 entries per file; users can change that limit. Users can edit, disable, or delete any source, including the defaults. Show the expanded URLs before saving so users can check which files will be fetched.

Seed the seven defaults below once. Save changes locally and do not recreate deleted sources after an app restart or upgrade. If the user deletes every source, keep the list empty. Fetch only from selected, enabled sources when the user starts a run. Here, “search a repository” means fetching the configured files; it does not require crawling the repository or signing in to GitHub.

Expand `{proto}` once per file key. The keys are case sensitive and can be protocol names, bundle names, or filenames. Keep the URL suffix exactly as configured; for example, `Eternity` has no `.txt` suffix. Treat update cadence and verification claims below as display labels supplied for the defaults. Every fetched config still goes through the same checks.

| Default source | HTTPS URL template | File keys |
| --- | --- | --- |
| barry-far/V2ray-Config (15-min updates) | `https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/{proto}.txt` | `vless`, `vmess`, `trojan`, `ss`, `ssr` |
| MatinGhanbari/v2ray-configs (15-min updates) | `https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/{proto}.txt` | `vless`, `vmess` |
| MahanKenway/Freedom-V2Ray (2-hour updates) | `https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/{proto}.txt` | `vless`, `vmess` |
| morpheusadam/v2ray-config (daily rebuild, verified) | `https://raw.githubusercontent.com/morpheusadam/v2ray-config/main/subs/bundles/{proto}.txt` | `best`, `all`, `iran` |
| Au1rxx/free-vpn-subscriptions (hourly updates) | `https://github.com/Au1rxx/free-vpn-subscriptions/raw/main/output/{proto}.txt` | `v2ray-base64` |
| Delta-Kronecker/V2ray-Config (daily updates) | `https://github.com/Delta-Kronecker/V2ray-Config/raw/refs/heads/main/config/{proto}.txt` | `all_configs` |
| mahdibland/V2RayAggregator (speed-tested nodes) | `https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/{proto}` | `Eternity` (Base64) |

## Implementation

Use upstream commit `d54313445d55e5a2ee6a23a692bebaeda07b40b6` as the reviewed starting point. The links below are pinned to that commit.

- [ ] **Set up the fork.** The local fork is based on the reviewed commit and has an `upstream` remote. The upstream GPL-3.0 license and notices are retained. Publishing the branch to a hosted fork is pending.
- [ ] **Add the desktop screen.** Wire a menu action into the Avalonia [main window](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/v2rayN.Desktop/Views/MainWindow.axaml). Keep the screen and its state separate from the existing profile list. Put the fetch, parse, compare, and test pipeline in `ServiceLib` so it can run without the UI. Allow one run per window and show a clear reason when a stage stops.
- [ ] **Save and resolve sources.** Use stable source IDs and a separate settings collection for the fetcher. Validate templates and their expanded HTTPS URLs before saving. Fetch each distinct URL once per run and retain its source labels. Report a missing file against its source and file key while processing the remaining files.
- [ ] **Fetch and parse into a staging area.** Reuse the download and share-link parsing logic where possible. The existing [subscription handler](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/ServiceLib/Handler/SubscriptionHandler.cs) calls `ConfigDbHandler.AddBatchServers`, which writes profiles. Expose a parse-only path instead of calling that method for a preview. Keep candidates and results in memory. Apply timeouts, a response size limit (including decoded content), a candidate count cap, and limited concurrency. Preserve HTTPS certificate checking, disallow redirects to non-HTTPS URLs, and report failures per source so other sources can finish.
- [ ] **Validate and deduplicate before testing.** Reject malformed links and missing or invalid protocol fields. Use protocol-aware identity that ignores display names, source labels, and UI metadata but includes credentials and all connection settings, such as transport, TLS, SNI, and path. Normalize only fields with known equivalent representations. If identity cannot be determined reliably, keep both candidates. Keep the first candidate in input order and record its other sources. Inspect the existing [dedup and cleanup code](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/ServiceLib/Handler/Data/ConfigDbHandler.cs) for reusable comparisons; do not call its deletion commands from this screen.
- [ ] **Test actual proxy traffic.** Adapt upstream [SpeedtestService](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/ServiceLib/Services/SpeedtestService.cs) and [CoreManager](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/ServiceLib/Manager/CoreManager.cs) for staged candidates. Some test paths look up profiles by database ID and write metrics to `ProfileExManager`; pass the staged profile directly and keep metrics in the run. Use a temporary loopback listener with the appropriate Xray or sing-box core, and send a small HTTPS request through it to a known test endpoint. A pass requires the expected response; a successful TCP connection to the proxy alone does not count. Start with four concurrent probes, a 10-second probe deadline, and one retry for transient failures. Stop test processes and remove temporary files on completion, error, or cancellation. Do not change the active proxy, system proxy, or TUN settings.
- [ ] **Classify failures.** A malformed profile is **Invalid**; an unhandled format or protocol is **Unsupported**. A completed probe that still fails after retry is **Unreachable** for this run. When the required core is missing, the test endpoint is unavailable, the run is canceled, or the result is otherwise inconclusive, mark the affected entries **Untested** and explain why. Exclude all of these from import; a failed test never deletes a saved profile.
- [ ] **Save reviewed results.** Recheck selection and duplicates against the destination group as part of the write, and commit the batch atomically through the profile storage layer. Refresh the existing profile list only after success. Preserve the last completed result view if a refresh fails or is canceled. Keep throughput tests, automated schedules, additional formats, and automatic pruning of saved profiles out of the first version.

## Verification

Use synthetic fixtures, a local subscription server, and controlled proxy and test endpoints. CI should not depend on live subscriptions.

| Area | It passes when |
| --- | --- |
| Repository sources | A fresh setup shows seven defaults that expand to 15 URLs. Adding, editing, disabling, and deleting sources works after restart, including deleting all defaults. A custom direct URL is accepted, a bad template is rejected, and a missing file does not stop the rest of the run. |
| Fetch and parse | Plain and Base64 lists produce the same profiles. Malformed entries and unsupported formats get separate reasons. A bad source, redirect, or oversized response does not block other sources. Diagnostics contain no credentials. |
| Deduplication | The same config with a different name is probed once, including across sources. The same host and port with different credentials, transport, or TLS settings stays distinct. |
| Connectivity | A working proxy passes and shows latency. A reachable port that cannot relay the test request fails. A missing core or unavailable test endpoint produces untested results. |
| Cancellation | Cancel during fetch and during probing. The UI responds, test processes and temporary files are cleaned up, and the active connection is unchanged. |
| Import | Only selected passing profiles are stored. A second import adds no duplicates. A simulated storage failure leaves no partial batch. Existing profiles and subscriptions are unchanged. |

From the fork root, run the desktop build and shared tests using the SDK required by [Directory.Build.props](https://github.com/2dust/v2rayN/blob/d54313445d55e5a2ee6a23a692bebaeda07b40b6/v2rayN/Directory.Build.props) (.NET 10 at the reviewed commit):

```sh
dotnet build v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj
dotnet test --project v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj
```

Finish with a Linux x64 desktop session: open the screen, run mixed fixtures, cancel and retry, import twice, and confirm the app still connects normally with the previously active profile.
