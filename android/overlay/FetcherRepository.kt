package com.v2ray.ang.ui.fetcher

import android.content.Context
import com.v2ray.ang.core.CoreNativeManager
import com.v2ray.ang.core.CoreOutboundBuilder
import com.v2ray.ang.dto.entities.ProfileItem
import com.v2ray.ang.dto.entities.SubscriptionItem
import com.v2ray.ang.handler.AngConfigManager
import com.v2ray.ang.handler.MmkvManager
import com.v2ray.ang.handler.SettingsChangeManager
import com.v2ray.ang.util.JsonUtil
import com.v2ray.ang.util.Utils
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.URI
import java.util.Locale
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

internal data class FetchSource(
    val id: String,
    val name: String,
    val url: String,
    val keys: List<String>,
    val enabled: Boolean,
) {
    fun urls(): List<URI> {
        require(name.isNotBlank() && keys.size <= 32)
        require(keys.distinct().size == keys.size && keys.all { it.matches(Regex("[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}")) })
        val hasTemplate = "{proto}" in url
        require((keys.isNotEmpty() == hasTemplate) && url.indexOf("{proto}") == url.lastIndexOf("{proto}"))
        if (hasTemplate) {
            val pathStart = url.indexOf('/', url.indexOf("://") + 3)
            require(pathStart >= 0 && url.indexOf("{proto}") > pathStart && "{proto}" !in url.substringAfter('?', ""))
        }
        return (if (hasTemplate) keys.map { url.replace("{proto}", it) } else listOf(url)).map {
            URI(it).also { uri -> require(uri.scheme == "https" && uri.host != null && uri.userInfo == null && uri.fragment == null) }
        }
    }
}

internal enum class FetchOutcome { Working, Duplicate, Invalid, Unsupported, Failed, DownloadError, Limited }

internal data class FetchRow(
    val id: Int,
    val source: String,
    val name: String,
    val outcome: FetchOutcome,
    val profile: ProfileItem? = null,
    val latency: Long? = null,
    val shareLink: String? = null,
)

internal class FetcherRepository(private val context: Context) {
    private val defaults by lazy {
        context.assets.open("taki_fetcher_sources.json").bufferedReader().use { reader ->
            requireNotNull(JsonUtil.fromJson(reader.readText(), Array<FetchSource>::class.java)).toList()
        }
    }

    private val http = OkHttpClient.Builder()
        .followSslRedirects(false)
        .callTimeout(15, TimeUnit.SECONDS)
        .build()

    fun sources(): List<FetchSource> = MmkvManager.decodeSettingsString(SOURCES)?.let {
        requireNotNull(JsonUtil.fromJson(it, Array<FetchSource>::class.java)).toList()
    } ?: defaults

    fun save(sources: List<FetchSource>) {
        check(MmkvManager.encodeSettings(SOURCES, JsonUtil.toJson(sources)))
    }

    fun restore(sources: List<FetchSource>): List<FetchSource> = missingDefaults(sources, defaults)

    suspend fun run(sources: List<FetchSource>, pasted: String, limit: Int, publish: (FetchRow) -> Unit) = coroutineScope {
        val seen = mutableSetOf<ProfileItem>()
        val downloads = sources.filter { it.enabled }.flatMap { source ->
            source.urls().mapIndexed { index, url ->
                (source.keys.getOrNull(index)?.let { "${source.name} / $it" } ?: source.name) to url
            }
        }.distinctBy { it.second }
        val ordinal = AtomicInteger()
        val permits = Semaphore(4)
        fun result(source: String, name: String, outcome: FetchOutcome, profile: ProfileItem? = null, latency: Long? = null,
                   shareLink: String? = null) {
            publish(FetchRow(ordinal.getAndIncrement(), source, name, outcome, profile, latency, shareLink))
        }
        CoreNativeManager.initCoreEnv(context)
        suspend fun process(label: String, body: String, entryLimit: Int): Unit = coroutineScope {
            val text = body.trim().removePrefix("\uFEFF")
            val lines = if ("://" in text) text else Utils.decode(text)
            val entries = lines.lineSequence().map(String::trim)
                .filter { it.isNotEmpty() && !it.startsWith('#') }.take(entryLimit + 1).iterator()
            if (!entries.hasNext()) {
                result(label, "", FetchOutcome.Invalid)
                return@coroutineScope
            }
            for (i in 0 until entryLimit) {
                if (!entries.hasNext()) break
                currentCoroutineContext().ensureActive()
                val line = entries.next()
                val scheme = line.substringBefore("://", "")
                val supported = scheme.lowercase(Locale.ROOT) in SCHEMES
                if (line.length > 65536 || !supported) {
                    result(label, "", if (scheme.isEmpty() || supported) FetchOutcome.Invalid else FetchOutcome.Unsupported)
                    continue
                }
                val link = scheme.lowercase(Locale.ROOT) + line.substring(scheme.length)
                val profile = AngConfigManager.parseFetcherProfile(link)
                if (profile == null || profile.server.isNullOrBlank() || (profile.serverPort?.toIntOrNull() ?: 0) !in 1..65535 ||
                    (scheme.lowercase(Locale.ROOT) !in SOCKS && profile.configType.name != "WIREGUARD" && profile.password.isNullOrBlank())) {
                    result(label, "", FetchOutcome.Invalid)
                    continue
                }
                val name = profile.remarks.filterNot(Char::isISOControl).take(100)
                    .takeUnless { it.contains("://") || (profile.password?.let { secret -> secret.length >= 4 && it.contains(secret) } == true) }
                    .orEmpty()
                if (!seen.add(identity(profile))) {
                    result(label, name, FetchOutcome.Duplicate)
                    continue
                }
                if (!profile.browserDialerMode.isNullOrBlank()) {
                    result(label, name, FetchOutcome.Unsupported)
                    continue
                }
                val outbound = CoreOutboundBuilder.convert(profile)
                if (outbound == null) {
                    result(label, name, FetchOutcome.Invalid)
                    continue
                }
                outbound.tag = "proxy"
                outbound.mux = null
                // The test core has exactly one outbound and no local listening sockets or direct fallback.
                val config = JsonUtil.toJson(mapOf("log" to mapOf("loglevel" to "none"), "outbounds" to listOf(outbound)))
                launch {
                    val delay = permits.withPermit { CoreNativeManager.measureOutboundDelay(config, TEST_URL) }
                    ensureActive()
                    if (delay >= 0) result(label, name, FetchOutcome.Working, profile, delay, link)
                    else result(label, name, FetchOutcome.Failed)
                }
            }
            if (entries.hasNext()) result(label, "", FetchOutcome.Limited)
        }

        val pending = ArrayDeque<Deferred<String?>>()
        var next = 0
        fun prefetch() {
            while (pending.size < DOWNLOAD_CONCURRENCY && next < downloads.size) {
                val url = downloads[next++].second
                pending.addLast(async {
                    try {
                        download(url)
                    } catch (_: IOException) {
                        null
                    }
                })
            }
        }
        prefetch()
        if (pasted.isNotBlank()) process("", pasted, MAX_PASTE_ENTRIES)
        for ((label, _) in downloads) {
            // Process in repository order so a faster duplicate cannot displace the first entry.
            val content = pending.removeFirst().await()
            prefetch()
            if (content == null) result(label, "", FetchOutcome.DownloadError)
            else process(label, content, limit)
        }
    }

    fun import(profiles: List<ProfileItem>, groupName: String): Int {
        val saved = MmkvManager.decodeServerList(GROUP)
            .mapNotNull(MmkvManager::decodeServerConfig).map(::identity).toMutableSet()
        val newProfiles = profiles.filter { saved.add(identity(it)) }
        if (newProfiles.isEmpty()) return 0
        if (MmkvManager.decodeSubscription(GROUP) == null) {
            MmkvManager.encodeSubscription(GROUP, SubscriptionItem(remarks = groupName, enabled = false))
        }
        val batch = newProfiles.associate { Utils.getUuid() to it.copy(subscriptionId = GROUP) }
        MmkvManager.saveServerProfiles(batch, emptyMap(), GROUP, append = true)
        SettingsChangeManager.makeSetupGroupTab()
        return batch.size
    }

    private suspend fun download(url: URI): String = suspendCancellableCoroutine { continuation ->
        val call = http.newCall(Request.Builder().url(url.toASCIIString()).build())
        continuation.invokeOnCancellation { call.cancel() }
        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) = continuation.resumeWithException(e)

            override fun onResponse(call: Call, response: Response) {
                val content = try {
                    response.use(::readBody)
                } catch (e: Exception) {
                    continuation.resumeWithException(e)
                    return
                }
                continuation.resume(content)
            }
        })
    }

    private fun readBody(response: Response): String {
        if (!response.isSuccessful) throw IOException("Repository download failed")
        val body = response.body ?: throw IOException("Missing repository response")
        if (body.contentLength() > MAX_BYTES) throw IOException("Repository exceeds size limit")
        val output = ByteArrayOutputStream()
        body.byteStream().use { input ->
            val buffer = ByteArray(8192)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                if (output.size() + read > MAX_BYTES) throw IOException("Repository exceeds size limit")
                output.write(buffer, 0, read)
            }
        }
        return output.toString(Charsets.UTF_8.name())
    }

    companion object {
        private const val SOURCES = "taki_fetcher_sources_v1"
        private const val GROUP = "taki-fetched-configs"
        private const val MAX_BYTES = 32 * 1024 * 1024
        private const val DOWNLOAD_CONCURRENCY = 4
        private const val MAX_PASTE_ENTRIES = 100
        private const val TEST_URL = "https://www.gstatic.com/generate_204"
        private val SOCKS = setOf("socks", "socks4", "socks5")
        private val SCHEMES = SOCKS + setOf("vmess", "vless", "ss", "trojan", "hysteria2", "hy2", "wireguard")

        internal fun missingDefaults(sources: List<FetchSource>, defaults: List<FetchSource>): List<FetchSource> {
            val existing = sources.map(FetchSource::id).toSet()
            return defaults.filter { it.id !in existing }
        }

        private fun identity(profile: ProfileItem) = profile.copy(
            subscriptionId = "", addedTime = 0L, remarks = "", description = null,
            server = profile.server?.lowercase(Locale.ROOT),
            serverPort = profile.serverPort?.toIntOrNull()?.toString(),
        )
    }
}
