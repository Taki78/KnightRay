package com.v2ray.ang.ui.fetcher

import com.v2ray.ang.core.CoreNativeManager
import com.v2ray.ang.core.CoreOutboundBuilder
import com.v2ray.ang.dto.entities.ProfileItem
import com.v2ray.ang.enums.EConfigType
import com.v2ray.ang.handler.MmkvManager
import com.v2ray.ang.util.JsonUtil
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal const val AUTO_SELECT_DEFAULT_SECONDS = 300
internal const val AUTO_SELECT_MIN_SECONDS = 5
internal const val AUTO_SELECT_MAX_SECONDS = 7200
internal const val KNIGHT_WEB_TEST_URL = "https://www.gstatic.com/generate_204"

internal fun <T> sortByLowestDelay(rows: List<T>, delayOf: (T) -> Long): List<T> =
    rows.sortedBy { delayOf(it).takeIf { delay -> delay > 0 } ?: Long.MAX_VALUE }

/** Builds a test with one outbound, no local listener and no direct fallback. */
internal fun buildKnightWebProbe(profile: ProfileItem): String? {
    if (profile.configType in listOf(EConfigType.CUSTOM, EConfigType.POLICYGROUP, EConfigType.PROXYCHAIN) ||
        !profile.browserDialerMode.isNullOrBlank() || profile.server.isNullOrBlank()) return null
    val subscription = profile.subscriptionId.takeIf(String::isNotEmpty)?.let(MmkvManager::decodeSubscription)
    if (!subscription?.prevProfile.isNullOrBlank() || !subscription?.nextProfile.isNullOrBlank()) return null
    val outbound = CoreOutboundBuilder.convert(profile) ?: return null
    outbound.tag = "proxy"
    outbound.mux = null
    return JsonUtil.toJson(mapOf("log" to mapOf("loglevel" to "none"), "outbounds" to listOf(outbound)))
}

internal data class AutoSelectResult(val id: String, val delayMs: Long)

internal fun fastestAutoConfig(results: List<AutoSelectResult>, activeId: String?) =
    results.asSequence().filter { it.delayMs >= 0 }
        .minWithOrNull(compareBy<AutoSelectResult> { it.delayMs }.thenBy { it.id != activeId }.thenBy { it.id })

internal sealed interface AutoSelectStatus {
    data object Ready : AutoSelectStatus
    data object Stopping : AutoSelectStatus
    data object Stopped : AutoSelectStatus
    data object Empty : AutoSelectStatus
    data object Unavailable : AutoSelectStatus
    data class Checking(val count: Int) : AutoSelectStatus
    data class Complete(
        val passed: Int, val tested: Int, val seconds: Int, val delayMs: Long?, val switched: Boolean,
    ) : AutoSelectStatus
}

internal data class AutoSelectState(
    val running: Boolean = false,
    val status: AutoSelectStatus = AutoSelectStatus.Ready,
)

// All session and selected-profile writes run on Main; native probes run on IO.
internal class AutoSelectSession(
    private val scope: CoroutineScope,
    private val activeId: () -> String?,
    private val activate: (String) -> Unit,
    private val publishDelays: suspend (String, Map<String, Long>) -> Unit,
) {
    private val mutableState = MutableStateFlow(AutoSelectState())
    val state = mutableState.asStateFlow()
    private var job: Job? = null

    fun start(groupId: String, ids: List<String>, seconds: Int) {
        if (job != null || ids.isEmpty() || seconds !in AUTO_SELECT_MIN_SECONDS..AUTO_SELECT_MAX_SECONDS) return
        val watched = ids.distinct()
        val run = scope.launch(start = CoroutineStart.LAZY) {
            try {
                while (true) {
                    val expected = activeId()
                    mutableState.value = AutoSelectState(true, AutoSelectStatus.Checking(watched.size))
                    val measurements = withContext(Dispatchers.IO) {
                        watched.chunked(4).flatMap { batch ->
                            ensureActive()
                            coroutineScope { batch.map { id -> async { probe(id) } }.awaitAll().filterNotNull() }
                        }
                    }
                    if (measurements.isEmpty()) {
                        mutableState.value = AutoSelectState(status = AutoSelectStatus.Empty)
                        return@launch
                    }
                    publishDelays(groupId, measurements.associate { it.second.id to it.second.delayMs })
                    val result = fastestAutoConfig(measurements.map { it.second }, expected)
                    val canApply = result != null && result.id != expected && withContext(Dispatchers.IO) {
                        val latest = MmkvManager.decodeServerConfig(result.id)
                        latest != null && latest == measurements.first { it.second.id == result.id }.first &&
                            buildKnightWebProbe(latest) != null
                    }
                    ensureActive()
                    val switched = canApply && activeId() == expected
                    if (switched && result != null) activate(result.id)
                    mutableState.value = AutoSelectState(
                        true, AutoSelectStatus.Complete(
                            measurements.count { it.second.delayMs >= 0 }, measurements.size, seconds, result?.delayMs, switched,
                        ),
                    )
                    delay(seconds * 1000L)
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                mutableState.value = AutoSelectState(status = AutoSelectStatus.Stopped)
                throw e
            } catch (_: Exception) {
                mutableState.value = AutoSelectState(status = AutoSelectStatus.Unavailable)
            } finally {
                job = null
            }
        }
        job = run
        run.start()
    }

    fun stop() {
        job?.let {
            mutableState.value = AutoSelectState(true, AutoSelectStatus.Stopping)
            it.cancel()
        }
    }

    private fun probe(id: String): Pair<ProfileItem, AutoSelectResult>? {
        val profile = MmkvManager.decodeServerConfig(id) ?: return null
        val config = buildKnightWebProbe(profile) ?: return null
        return profile to AutoSelectResult(id, CoreNativeManager.measureOutboundDelay(config, KNIGHT_WEB_TEST_URL))
    }
}
