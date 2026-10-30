package com.nauralta.augora.tv.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Button
import androidx.tv.material3.Card
import androidx.tv.material3.Icon
import androidx.tv.material3.IconButton
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Tab
import androidx.tv.material3.TabRow
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.MediaItem
import kotlinx.coroutines.launch

private data class Folder(val uri: String, val title: String)

/** The app after logging in: tabs (home, search, queue), folders on top of them, the player bar and the player screen. */
@Composable
fun MainScreen(app: AugoraApp) {
    var tab by remember { mutableIntStateOf(0) }
    val folders = remember { mutableStateListOf<Folder>() }
    var showNowPlaying by remember { mutableStateOf(false) }
    var showPlayers by remember { mutableStateOf(false) }

    val open: (MediaItem) -> Unit = { folders.add(Folder(it.uri, it.name)) }

    BackHandler(enabled = folders.isNotEmpty() && !showNowPlaying && !showPlayers) { folders.removeLast() }

    Box(Modifier.fillMaxSize().background(Background)) {
        Column(Modifier.fillMaxSize()) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 48.dp, vertical = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                TabRow(selectedTabIndex = tab, modifier = Modifier.weight(1f)) {
                    listOf(S.home, S.search, S.queue).forEachIndexed { index, title ->
                        Tab(
                            selected = tab == index,
                            onFocus = {
                                if (tab != index) {
                                    tab = index
                                    folders.clear()
                                }
                            },
                            onClick = {},
                        ) { Text(title, modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp)) }
                    }
                }
                Button(onClick = { app.logout() }) { Text(S.logOut) }
            }

            Box(Modifier.weight(1f)) {
                val folder = folders.lastOrNull()
                when {
                    folder != null -> BrowseScreen(app, folder.uri, folder.title, open)
                    tab == 0 -> BrowseScreen(app, null, S.home, open)
                    tab == 1 -> SearchScreen(app, open)
                    else -> QueueScreen(app)
                }
            }

            PlayerBar(app, onOpen = { showNowPlaying = true }, onPickPlayer = { showPlayers = true })
        }

        if (showNowPlaying) {
            NowPlayingScreen(app, onClose = { showNowPlaying = false }, onPickPlayer = { showPlayers = true })
        }
        if (showPlayers) PlayerPicker(app) { showPlayers = false }
    }
}

/** The bar at the bottom: what plays on which player, with play/pause. */
@Composable
private fun PlayerBar(app: AugoraApp, onOpen: () -> Unit, onPickPlayer: () -> Unit) {
    val players by app.live.players.collectAsState()
    val selectedId by app.live.selectedId.collectAsState()
    val player = players[selectedId]
    val now = player?.nowPlaying
    val scope = rememberCoroutineScope()

    Card(onClick = onOpen, modifier = Modifier.fillMaxWidth().padding(horizontal = 48.dp, vertical = 12.dp)) {
        Row(Modifier.padding(10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Cover(app.api.resolve(now?.imageUrl), Modifier.size(56.dp))
            Column(Modifier.weight(1f)) {
                Text(now?.title ?: S.nothingPlaying, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                Text(
                    player?.let { if (it.id == app.session.devicePlayerId) S.thisTv(it.name) else it.name }.orEmpty(),
                    color = TextSecondary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            IconButton(onClick = onPickPlayer) { Icon(Icons.Speaker, contentDescription = null, tint = Accent, modifier = Modifier.size(24.dp)) }
            IconButton(onClick = {
                scope.launch { runCatching { if (player?.isPlaying == true) app.api.pause(selectedId) else app.api.resume(selectedId) } }
            }) {
                Icon(if (player?.isPlaying == true) Icons.Pause else Icons.Play, contentDescription = null, modifier = Modifier.size(28.dp))
            }
        }
    }
}

/** Where music plays: this TV or another player. */
@Composable
private fun PlayerPicker(app: AugoraApp, onClose: () -> Unit) {
    val players by app.live.players.collectAsState()
    val selectedId by app.live.selectedId.collectAsState()
    val scope = rememberCoroutineScope()
    val own = app.session.devicePlayerId

    val rows = players.values
        .filter { it.available || it.id == selectedId }
        .sortedWith(compareByDescending<com.nauralta.augora.tv.data.Player> { it.id == own }.thenBy { it.name.lowercase() })

    Dialog(onDismissRequest = onClose) {
        Column(
            Modifier.clip(RoundedCornerShape(16.dp)).background(Surface).padding(24.dp).width(600.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(S.playOn, style = MaterialTheme.typography.titleLarge)
            if (rows.none { it.id == own }) {
                // The own player registers a moment after the app starts.
                Text(S.thisTv(app.session.deviceName), color = TextSecondary)
            }
            rows.forEach { player ->
                Card(
                    onClick = {
                        scope.launch { app.live.select(player.id) }
                        onClose()
                    },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        Icon(Icons.Speaker, contentDescription = null, tint = TextSecondary, modifier = Modifier.size(24.dp))
                        Column(Modifier.weight(1f)) {
                            Text(if (player.id == own) S.thisTv(player.name) else player.name)
                            val line = if (!player.available) S.unavailable else player.nowPlaying?.title.takeIf { player.isPlaying }
                            if (line != null) Text(line, color = TextSecondary, style = MaterialTheme.typography.bodySmall, maxLines = 1)
                        }
                        if (player.id == selectedId) Icon(Icons.Check, contentDescription = null, tint = Accent, modifier = Modifier.size(24.dp))
                    }
                }
            }
        }
    }
}
