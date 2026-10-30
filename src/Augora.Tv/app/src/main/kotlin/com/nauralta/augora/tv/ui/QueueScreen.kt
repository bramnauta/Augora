package com.nauralta.augora.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.Card
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import kotlinx.coroutines.launch

/** The queue of the selected player: choose an item to play it, long press to remove it. */
@Composable
fun QueueScreen(app: AugoraApp) {
    val queue by app.live.queue.collectAsState()
    val playerId by app.live.selectedId.collectAsState()
    val scope = rememberCoroutineScope()
    val items = queue?.items.orEmpty()

    Column(Modifier.fillMaxSize().padding(horizontal = 48.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
            Text(S.queue, style = MaterialTheme.typography.headlineMedium)
            if (items.isNotEmpty()) {
                Button(onClick = { scope.launch { runCatching { app.api.clearQueue(playerId) } } }) { Text(S.clearQueue) }
            }
        }
        if (items.isEmpty()) {
            Text(S.queueEmpty)
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
                items(items, key = { it.id }) { entry ->
                    val current = queue?.items?.indexOf(entry) == queue?.currentIndex
                    Card(
                        onClick = { scope.launch { runCatching { app.api.playQueueItem(playerId, entry.id) } } },
                        onLongClick = { scope.launch { runCatching { app.api.removeQueueItem(playerId, entry.id) } } },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Row(
                            Modifier.padding(10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(16.dp),
                        ) {
                            Cover(app.api.resolve(entry.item.imageUrl), Modifier.size(56.dp))
                            Column(Modifier.weight(1f)) {
                                Text(
                                    entry.item.name,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    color = if (current) Accent else androidx.compose.ui.graphics.Color.White,
                                    style = MaterialTheme.typography.titleMedium,
                                )
                                Text(
                                    entry.item.subtitle.orEmpty(),
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    color = TextSecondary,
                                    style = MaterialTheme.typography.bodySmall,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
