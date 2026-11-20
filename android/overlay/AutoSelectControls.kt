package com.v2ray.ang.ui.fetcher

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.v2ray.ang.R
import com.v2ray.ang.dto.entities.ServersCache

@Composable
internal fun AutoSelectControls(session: AutoSelectSession, groupId: String, servers: List<ServersCache>, canStart: Boolean) {
    val state by session.state.collectAsStateWithLifecycle()
    var editing by rememberSaveable { mutableStateOf(false) }
    var interval by rememberSaveable { mutableStateOf(AUTO_SELECT_DEFAULT_SECONDS.toString()) }
    var selectedOnly by rememberSaveable { mutableStateOf(false) }
    var selected by rememberSaveable { mutableStateOf(arrayListOf<String>()) }
    val seconds = interval.toIntOrNull()?.takeIf { it in AUTO_SELECT_MIN_SECONDS..AUTO_SELECT_MAX_SECONDS }
    val ids = servers.map { it.guid }.filter { !selectedOnly || it in selected }

    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = { if (state.running) session.stop() else if (seconds != null) session.start(groupId, ids, seconds) },
                enabled = state.status != AutoSelectStatus.Stopping && (state.running || (canStart && ids.isNotEmpty() && seconds != null)),
            ) {
                val label = if (state.running) R.string.knight_auto_stop else R.string.knight_auto_start
                Text(stringResource(label))
            }
            TextButton(onClick = { editing = true }, enabled = !state.running) {
                Text(stringResource(R.string.knight_auto_settings, interval))
            }
        }
        Text(
            if (state.status == AutoSelectStatus.Ready) {
                stringResource(if (selectedOnly) R.string.knight_auto_selected_count else R.string.knight_auto_all_count, ids.size)
            } else {
                autoStatusLabel(state.status)
            },
        )
    }

    if (editing) {
        AlertDialog(
            onDismissRequest = { editing = false },
            title = { Text(stringResource(R.string.knight_auto_options)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(
                        value = interval,
                        onValueChange = { value -> interval = value.filter { it in '0'..'9' }.take(4) },
                        label = { Text(stringResource(R.string.knight_auto_seconds)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        isError = seconds == null,
                        supportingText = { Text(stringResource(R.string.knight_auto_range)) },
                    )
                    Row(
                        Modifier.fillMaxWidth().toggleable(selectedOnly, role = Role.Checkbox) { selectedOnly = it },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Checkbox(selectedOnly, onCheckedChange = null, modifier = Modifier.padding(end = 8.dp))
                        Text(stringResource(R.string.knight_auto_selected_only))
                    }
                    if (selectedOnly) {
                        LazyColumn(Modifier.heightIn(max = 240.dp)) {
                            items(servers.distinctBy { it.guid }, key = { it.guid }) { server ->
                                val checked = server.guid in selected
                                Row(
                                    Modifier.fillMaxWidth().toggleable(checked, role = Role.Checkbox) {
                                        selected = ArrayList(if (it) selected + server.guid else selected - server.guid)
                                    }.padding(vertical = 8.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Checkbox(checked, onCheckedChange = null, modifier = Modifier.padding(end = 8.dp))
                                    Text(server.profile.remarks.ifBlank { server.profile.configType.name }, maxLines = 2)
                                }
                            }
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { editing = false }, enabled = seconds != null) {
                    Text(stringResource(android.R.string.ok))
                }
            },
        )
    }
}

@Composable
private fun autoStatusLabel(status: AutoSelectStatus): String = when (status) {
    AutoSelectStatus.Ready -> ""
    AutoSelectStatus.Stopping -> stringResource(R.string.knight_auto_stopping)
    AutoSelectStatus.Stopped -> stringResource(R.string.knight_auto_stopped)
    AutoSelectStatus.Empty -> stringResource(R.string.knight_auto_empty)
    AutoSelectStatus.Unavailable -> stringResource(R.string.knight_auto_unavailable)
    is AutoSelectStatus.Checking -> stringResource(R.string.knight_auto_checking, status.count)
    is AutoSelectStatus.Complete -> {
        val detail = if (status.delayMs == null) stringResource(R.string.knight_auto_no_working)
        else stringResource(if (status.switched) R.string.knight_auto_switched else R.string.knight_auto_best, status.delayMs)
        stringResource(R.string.knight_auto_complete, status.passed, status.tested, detail, status.seconds)
    }
}
