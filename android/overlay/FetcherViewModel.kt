package com.v2ray.ang.ui.fetcher

import android.app.Application
import androidx.annotation.StringRes
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.v2ray.ang.AppConfig
import com.v2ray.ang.R
import com.v2ray.ang.util.LogUtil
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.withContext
import java.util.UUID

internal enum class FetchScope { Selected, All, Pasted }

internal data class FetchEditor(val name: String = "", val url: String = "", val keys: String = "", val enabled: Boolean = true, val id: String? = null)

internal data class FetcherState(
    val sources: List<FetchSource> = emptyList(),
    val selected: String? = null,
    val loaded: Boolean = false,
    val editor: FetchEditor? = null,
    val scope: FetchScope = FetchScope.All,
    val pasted: String = "",
    val limit: String = "35",
    val results: List<FetchRow> = emptyList(),
    val selectedResults: Set<Int> = emptySet(),
    val running: Boolean = false,
    val writing: Boolean = false,
    val showResults: Boolean = false,
    @param:StringRes val notice: Int? = null,
    val quantity: Int? = null,
)

internal sealed interface FetchAction {
    data class Select(val id: String) : FetchAction
    data class Scope(val value: FetchScope) : FetchAction
    data class Pasted(val value: String) : FetchAction
    data class Limit(val value: String) : FetchAction
    data class Edit(val value: FetchEditor?) : FetchAction
    data class Results(val visible: Boolean) : FetchAction
    data class SelectResult(val id: Int) : FetchAction
    data object SelectAll : FetchAction
    data object Restore : FetchAction
    data object Delete : FetchAction
    data object Save : FetchAction
    data object Run : FetchAction
    data object Cancel : FetchAction
    data object Import : FetchAction
}

internal class FetcherViewModel(app: Application) : AndroidViewModel(app) {
    private val repository = FetcherRepository(app)
    private val mutableState = MutableStateFlow(FetcherState())
    val state = mutableState.asStateFlow()
    private var run: Job? = null

    init {
        viewModelScope.launch {
            try {
                val sources = withContext(Dispatchers.IO) { repository.sources() }
                mutableState.update { it.copy(sources = sources, selected = sources.firstOrNull(FetchSource::enabled)?.id, loaded = true) }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                notice(R.string.taki_fetch_error)
            }
        }
    }

    fun onAction(action: FetchAction) {
        val current = state.value
        when (action) {
            is FetchAction.Select -> mutableState.update { it.copy(selected = action.id) }
            is FetchAction.Scope -> mutableState.update { it.copy(scope = action.value) }
            is FetchAction.Pasted -> if (action.value.length <= 250_000) mutableState.update { it.copy(pasted = action.value) }
            is FetchAction.Limit -> if (action.value.length <= 3) mutableState.update { it.copy(limit = action.value) }
            is FetchAction.Edit -> mutableState.update { it.copy(editor = action.value, notice = null) }
            is FetchAction.Results -> mutableState.update { it.copy(showResults = action.visible) }
            is FetchAction.SelectResult -> mutableState.update {
                val selected = it.selectedResults
                it.copy(selectedResults = if (action.id in selected) selected - action.id else selected + action.id)
            }
            FetchAction.SelectAll -> mutableState.update {
                it.copy(selectedResults = it.results.filter { row -> row.outcome == FetchOutcome.Working }.map { row -> row.id }.toSet())
            }
            FetchAction.Restore -> write {
                val missing = repository.restore(current.sources)
                if (missing.isNotEmpty()) repository.save(current.sources + missing)
                val message = if (missing.isEmpty()) R.string.taki_fetch_all_present else R.string.taki_fetch_restored;
                { state: FetcherState -> state.copy(sources = current.sources + missing, selected = state.selected ?: missing.firstOrNull()?.id,
                    notice = message, quantity = missing.size.takeIf { it > 0 }) }
            }
            FetchAction.Delete -> if (current.selected != null) write {
                val sources = current.sources.filterNot { it.id == current.selected }
                repository.save(sources);
                { state: FetcherState -> state.copy(sources = sources, selected = sources.firstOrNull(FetchSource::enabled)?.id, editor = null, notice = null) }
            }
            FetchAction.Save -> {
                val editor = current.editor ?: return
                val source = FetchSource(editor.id ?: UUID.randomUUID().toString(), editor.name.trim(), editor.url.trim(),
                    editor.keys.split(',').map(String::trim).filter(String::isNotEmpty), editor.enabled)
                if (runCatching { source.urls() }.isFailure) {
                    notice(R.string.taki_fetch_bad_source)
                    return
                }
                write {
                    val sources = if (editor.id == null) current.sources + source else current.sources.map { if (it.id == source.id) source else it }
                    repository.save(sources);
                    { state: FetcherState -> state.copy(sources = sources, selected = source.id, editor = null, notice = null) }
                }
            }
            FetchAction.Run -> start(current)
            FetchAction.Cancel -> if (current.running) {
                notice(R.string.taki_fetch_canceling)
                run?.cancel()
            }
            FetchAction.Import -> {
                val profiles = current.results.filter { it.id in current.selectedResults && it.outcome == FetchOutcome.Working }
                    .mapNotNull(FetchRow::profile)
                if (profiles.isEmpty() || current.running) return
                write {
                    val count = repository.import(profiles, getApplication<Application>().getString(R.string.taki_fetch_group));
                    { state: FetcherState -> state.copy(notice = R.string.taki_fetch_imported, quantity = count, selectedResults = emptySet()) }
                }
            }
        }
    }

    private fun start(current: FetcherState) {
        if (current.running || current.writing || !current.loaded) return
        val limit = current.limit.toIntOrNull() ?: 0
        val selected = when (current.scope) {
            FetchScope.Selected -> current.sources.filter { it.enabled && it.id == current.selected }
            FetchScope.All -> current.sources.filter(FetchSource::enabled)
            FetchScope.Pasted -> emptyList()
        }
        val pasted = if (current.scope == FetchScope.Pasted) current.pasted else ""
        when {
            limit !in 1..100 -> { notice(R.string.taki_fetch_bad_limit); return }
            current.scope == FetchScope.Pasted && pasted.isBlank() -> { notice(R.string.taki_fetch_need_links); return }
            current.scope != FetchScope.Pasted && selected.isEmpty() -> { notice(R.string.taki_fetch_need_source); return }
        }
        mutableState.update { it.copy(running = true, showResults = true, results = emptyList(), selectedResults = emptySet(),
            notice = R.string.taki_fetch_loading, quantity = null) }
        run = viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    repository.run(selected, pasted, limit) { row -> mutableState.update { it.copy(results = it.results + row) } }
                }
                notice(null)
            } catch (_: CancellationException) {
                notice(R.string.taki_fetch_cancelled)
            } catch (e: Exception) {
                LogUtil.e(AppConfig.TAG, "Android fetcher run: ${e.javaClass.simpleName}")
                notice(R.string.taki_fetch_error)
            } finally {
                mutableState.update { it.copy(running = false) }
            }
        }
    }

    private fun write(block: () -> (FetcherState) -> FetcherState) {
        if (state.value.writing || state.value.running || !state.value.loaded) return
        mutableState.update { it.copy(writing = true) }
        viewModelScope.launch {
            try {
                val update = withContext(Dispatchers.IO) { block() }
                mutableState.update(update)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                LogUtil.e(AppConfig.TAG, "Android fetcher storage: ${e.javaClass.simpleName}")
                notice(R.string.taki_fetch_error)
            } finally {
                mutableState.update { it.copy(writing = false) }
            }
        }
    }

    private fun notice(@StringRes text: Int?) {
        mutableState.update { it.copy(notice = text, quantity = null) }
    }
}
