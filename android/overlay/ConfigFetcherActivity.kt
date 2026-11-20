package com.v2ray.ang.ui.fetcher

import android.graphics.Bitmap
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.v2ray.ang.R
import com.v2ray.ang.ui.base.BaseComponentActivity
import com.v2ray.ang.ui.compose.AppTopBar
import com.v2ray.ang.ui.compose.QRCodeDialog
import com.v2ray.ang.util.QRCodeDecoder
import com.v2ray.ang.util.Utils
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class ConfigFetcherActivity : BaseComponentActivity() {
    private val viewModel: FetcherViewModel by viewModels()

    @Composable
    override fun ScreenContent() {
        val state by viewModel.state.collectAsStateWithLifecycle()
        FetcherScreen(state, viewModel::onAction, ::finish)
    }
}

@Composable
private fun FetcherScreen(state: FetcherState, action: (FetchAction) -> Unit, back: () -> Unit) {
    var showOther by rememberSaveable { mutableStateOf(false) }
    var shareLink by remember { mutableStateOf<String?>(null) }
    val idle = state.loaded && !state.running && !state.writing
    val working = state.results.count { it.outcome == FetchOutcome.Working }
    Scaffold(topBar = { AppTopBar(stringResource(R.string.taki_fetch_title), back, isLoading = state.running) }) { insets ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(insets).consumeWindowInsets(insets).imePadding().padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(!state.showResults, { action(FetchAction.Results(false)) },
                        { Text(stringResource(R.string.taki_fetch_sources)) })
                    FilterChip(state.showResults, { action(FetchAction.Results(true)) },
                        { Text(stringResource(R.string.taki_fetch_results)) })
                }
            }
            state.notice?.let { message ->
                item {
                    val quantity = state.quantity
                    Text(if (quantity == null) stringResource(message) else stringResource(message, quantity))
                }
            }
            if (state.showResults) {
                item {
                    Text(stringResource(R.string.taki_fetch_counts, working, state.results.size - working))
                    if (state.running) {
                        TextButton({ action(FetchAction.Cancel) }, enabled = state.notice != R.string.taki_fetch_canceling) {
                            Text(stringResource(R.string.taki_fetch_cancel))
                        }
                    }
                    if (working == 0) Text(stringResource(R.string.taki_fetch_no_working))
                    if (working > 0) {
                        TextButton({ action(FetchAction.SelectAll) }) { Text(stringResource(R.string.taki_fetch_select_all)) }
                        Button({ action(FetchAction.Import) }, enabled = idle && state.selectedResults.isNotEmpty()) {
                            Text(stringResource(R.string.taki_fetch_import))
                        }
                    }
                    FilterChip(showOther, { showOther = !showOther }, { Text(stringResource(R.string.taki_fetch_other)) })
                }
                val results = state.results.filter { (it.outcome != FetchOutcome.Working) == showOther }
                items(if (showOther) results else results.sortedBy { it.latency ?: Long.MAX_VALUE }, key = { "result-${it.id}" }) { row ->
                    val passed = row.outcome == FetchOutcome.Working
                    val checked = row.id in state.selectedResults
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Row(Modifier.weight(1f).then(if (passed) Modifier.toggleable(checked, role = Role.Checkbox,
                            onValueChange = { action(FetchAction.SelectResult(row.id)) }) else Modifier).padding(vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            if (passed) Checkbox(checked, onCheckedChange = null, modifier = Modifier.padding(end = 12.dp))
                            Column {
                                Text(row.name.ifBlank { row.source.ifBlank { stringResource(R.string.taki_fetch_unnamed) } })
                                Text(outcomeLabel(row), style = MaterialTheme.typography.bodySmall)
                                if (row.name.isNotBlank() && row.source.isNotBlank()) Text(row.source, style = MaterialTheme.typography.bodySmall)
                            }
                        }
                        row.shareLink?.let { link ->
                            TextButton(onClick = { shareLink = link }) { Text(stringResource(R.string.taki_fetch_share_qr)) }
                        }
                    }
                    HorizontalDivider()
                }
            } else {
                item {
                    val selected = state.sources.firstOrNull { it.id == state.selected }
                    Button({ showOther = false; action(FetchAction.Run) }, enabled = idle && state.editor == null) {
                        Text(stringResource(R.string.taki_fetch_run))
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                        listOf(FetchScope.Selected to R.string.taki_fetch_selected, FetchScope.All to R.string.taki_fetch_all,
                            FetchScope.Pasted to R.string.taki_fetch_paste).forEach { (scope, label) ->
                            FilterChip(state.scope == scope, { action(FetchAction.Scope(scope)) },
                                { Text(stringResource(label)) }, enabled = idle)
                        }
                    }
                    if (state.scope == FetchScope.Pasted) {
                        OutlinedTextField(state.pasted, { action(FetchAction.Pasted(it)) },
                            modifier = Modifier.fillMaxWidth(), enabled = idle, minLines = 3, maxLines = 6,
                            label = { Text(stringResource(R.string.taki_fetch_paste_hint)) })
                    } else {
                        OutlinedTextField(state.limit, { action(FetchAction.Limit(it)) },
                            modifier = Modifier.fillMaxWidth(), enabled = idle, singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            label = { Text(stringResource(R.string.taki_fetch_entries)) })
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        TextButton({ action(FetchAction.Edit(FetchEditor())) }, enabled = idle) { Text(stringResource(R.string.taki_fetch_new)) }
                        TextButton({ action(FetchAction.Restore) }, enabled = idle) { Text(stringResource(R.string.taki_fetch_restore)) }
                    }
                    if (selected != null) {
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                            TextButton({
                                action(FetchAction.Edit(FetchEditor(selected.name, selected.url, selected.keys.joinToString(", "),
                                    selected.enabled, selected.id)))
                            }, enabled = idle) { Text(stringResource(R.string.taki_fetch_edit)) }
                            TextButton({ action(FetchAction.Delete) }, enabled = idle) { Text(stringResource(R.string.taki_fetch_delete)) }
                        }
                    }
                    state.editor?.let { Editor(it, idle, action) }
                }
                items(state.sources, key = { it.id }) { source ->
                    val selected = source.id == state.selected
                    Row(Modifier.fillMaxWidth().selectable(selected, enabled = idle && state.editor == null, role = Role.RadioButton,
                        onClick = { action(FetchAction.Select(source.id)) }).padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected, onClick = null, modifier = Modifier.padding(end = 12.dp))
                        Text(if (source.enabled) source.name else stringResource(R.string.taki_fetch_disabled, source.name))
                    }
                    HorizontalDivider()
                }
            }
        }
    }
    shareLink?.let { FetcherQrDialog(it) { shareLink = null } }
}

@Composable
private fun FetcherQrDialog(link: String, dismiss: () -> Unit) {
    val qr by produceState<Result<Bitmap>?>(null, link) {
        value = withContext(Dispatchers.Default) { runCatching { checkNotNull(QRCodeDecoder.createQRCode(link)) } }
    }
    val bitmap = qr?.getOrNull()
    if (bitmap != null) {
        QRCodeDialog(bitmap, dismiss)
    } else {
        val context = LocalContext.current
        val failed = qr?.isFailure == true
        AlertDialog(
            onDismissRequest = dismiss,
            title = { Text(stringResource(R.string.taki_fetch_share_qr)) },
            text = { Text(stringResource(if (failed) R.string.taki_fetch_qr_failed else R.string.taki_fetch_qr_loading)) },
            confirmButton = {
                if (failed) TextButton(onClick = { Utils.setClipboard(context, link); dismiss() }) {
                    Text(stringResource(R.string.taki_fetch_qr_copy_link))
                }
            },
            dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.action_close)) } },
        )
    }
}

@Composable
private fun Editor(value: FetchEditor, enabled: Boolean, action: (FetchAction) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        OutlinedTextField(value.name, { action(FetchAction.Edit(value.copy(name = it))) }, Modifier.fillMaxWidth(),
            enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.taki_fetch_name)) })
        OutlinedTextField(value.url, { action(FetchAction.Edit(value.copy(url = it))) }, Modifier.fillMaxWidth(),
            enabled = enabled, singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            label = { Text(stringResource(R.string.taki_fetch_url)) })
        OutlinedTextField(value.keys, { action(FetchAction.Edit(value.copy(keys = it))) }, Modifier.fillMaxWidth(),
            enabled = enabled, singleLine = true, label = { Text(stringResource(R.string.taki_fetch_keys)) })
        Row(Modifier.toggleable(value.enabled, enabled = enabled, role = Role.Checkbox,
            onValueChange = { action(FetchAction.Edit(value.copy(enabled = it))) }).padding(vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Checkbox(value.enabled, onCheckedChange = null, modifier = Modifier.padding(end = 12.dp))
            Text(stringResource(R.string.taki_fetch_enabled))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Button({ action(FetchAction.Save) }, enabled = enabled) { Text(stringResource(R.string.taki_fetch_save)) }
            TextButton({ action(FetchAction.Edit(null)) }, enabled = enabled) { Text(stringResource(R.string.taki_fetch_cancel)) }
        }
    }
}

@Composable
private fun outcomeLabel(row: FetchRow): String = when (row.outcome) {
    FetchOutcome.Working -> stringResource(R.string.taki_fetch_working, row.latency ?: 0L)
    FetchOutcome.Duplicate -> stringResource(R.string.taki_fetch_duplicate)
    FetchOutcome.Invalid -> stringResource(R.string.taki_fetch_invalid)
    FetchOutcome.Unsupported -> stringResource(R.string.taki_fetch_unsupported)
    FetchOutcome.Failed -> stringResource(R.string.taki_fetch_failed)
    FetchOutcome.DownloadError -> stringResource(R.string.taki_fetch_download_error)
    FetchOutcome.Limited -> stringResource(R.string.taki_fetch_limited)
}
