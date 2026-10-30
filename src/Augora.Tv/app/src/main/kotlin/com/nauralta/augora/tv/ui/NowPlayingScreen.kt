package com.nauralta.augora.tv.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.Icon
import androidx.tv.material3.IconButton
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.formatTime
import com.nauralta.augora.tv.data.parseTimeSpan
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** The full player screen: cover, title, progress and the controls of the selected player. */
@Composable
fun NowPlayingScreen(app: AugoraApp, onClose: () -> Unit, onPickPlayer: () -> Unit) {
    BackHandler(onBack = onClose)

    val players by app.live.players.collectAsState()
    val selectedId by app.live.selectedId.collectAsState()
    val queue by app.live.queue.collectAsState()
    val player = players[selectedId]
    val now = player?.nowPlaying
    val scope = rememberCoroutineScope()
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }

    // The reported position plus the time played since it arrived: players report it only now and then.
    var tick by remember { mutableLongStateOf(0) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(1000)
            tick++
        }
    }
    val receivedAt = remember(player) { System.currentTimeMillis() }
    val duration = parseTimeSpan(now?.duration) ?: 0.0
    val reported = parseTimeSpan(now?.position) ?: 0.0
    val position = if (tick >= 0 && player?.isPlaying == true) {
        minOf(duration.takeIf { it > 0 } ?: Double.MAX_VALUE, reported + (System.currentTimeMillis() - receivedAt) / 1000.0)
    } else {
        reported
    }

    fun command(block: suspend () -> Unit) {
        scope.launch { runCatching { block() } }
    }

    Box(Modifier.fillMaxSize().background(Background)) {
        Row(Modifier.fillMaxSize().padding(64.dp), horizontalArrangement = Arrangement.spacedBy(64.dp), verticalAlignment = Alignment.CenterVertically) {
            Cover(app.api.resolve(now?.imageUrl), Modifier.size(360.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(player?.let { playingOn(app, it.id, it.name) }.orEmpty(), color = TextSecondary)
                Text(now?.title ?: S.nothingPlaying, style = MaterialTheme.typography.headlineLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(now?.subtitle.orEmpty(), color = TextSecondary, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)

                if (now != null && !now.isLive && duration > 0) {
                    Spacer(Modifier.height(8.dp))
                    LinearProgressIndicator(
                        progress = { (position / duration).toFloat().coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth(),
                        color = Accent,
                        trackColor = SurfaceRaised,
                    )
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(formatTime(position), color = TextSecondary)
                        Text(formatTime(duration), color = TextSecondary)
                    }
                }

                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    val shuffle = queue?.shuffle == true
                    val repeat = queue?.repeat ?: "Off"
                    ControlButton(Icons.Shuffle, tint = if (shuffle) Accent else Color.White) {
                        command { app.api.setShuffle(selectedId, !shuffle) }
                    }
                    ControlButton(Icons.Previous) { command { app.api.previous(selectedId) } }
                    ControlButton(
                        if (player?.isPlaying == true) Icons.Pause else Icons.Play,
                        modifier = Modifier.focusRequester(focus),
                    ) {
                        command { if (player?.isPlaying == true) app.api.pause(selectedId) else app.api.resume(selectedId) }
                    }
                    ControlButton(Icons.Next) { command { app.api.next(selectedId) } }
                    ControlButton(if (repeat == "One") Icons.RepeatOne else Icons.Repeat, tint = if (repeat == "Off") Color.White else Accent) {
                        command { app.api.setRepeat(selectedId, when (repeat) { "Off" -> "All"; "All" -> "One"; else -> "Off" }) }
                    }
                }

                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Button(onClick = onPickPlayer) {
                        Icon(Icons.Speaker, contentDescription = null, modifier = Modifier.size(20.dp))
                        Text("  " + S.playOn, maxLines = 1)
                    }
                    // Track radio: keep playing similar tracks when the queue ends.
                    val autoplay = queue?.autoplay == true
                    ControlButton(Icons.Autoplay, tint = if (autoplay) Accent else Color.White) {
                        command { app.api.setAutoplay(selectedId, !autoplay) }
                    }
                    if (now != null && !now.isLive && player?.canSeek == true) {
                        Button(onClick = { command { app.api.seek(selectedId, maxOf(0.0, position - 10)) } }) { Text("−10 s") }
                        Button(onClick = { command { app.api.seek(selectedId, position + 10) } }) { Text("+10 s") }
                    }
                    // This TV's own volume is set with the remote; other players are set here.
                    if (player != null && selectedId != app.session.devicePlayerId && player.canSetVolume) {
                        ControlButton(Icons.Minus) { command { app.api.setVolume(selectedId, (player.volume - 0.05).coerceAtLeast(0.0)) } }
                        Text("${(player.volume * 100).toInt()}%")
                        ControlButton(Icons.Plus) { command { app.api.setVolume(selectedId, (player.volume + 0.05).coerceAtMost(1.0)) } }
                    }
                }
            }
        }
    }
}

private fun playingOn(app: AugoraApp, id: String, name: String) =
    S.playingOn(if (id == app.session.devicePlayerId) S.thisTv(name) else name)

@Composable
private fun ControlButton(icon: ImageVector, modifier: Modifier = Modifier, tint: Color = Color.White, onClick: () -> Unit) {
    IconButton(onClick = onClick, modifier = modifier.size(64.dp)) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(32.dp))
    }
}
