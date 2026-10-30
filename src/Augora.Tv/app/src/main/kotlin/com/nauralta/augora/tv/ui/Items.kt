package com.nauralta.augora.tv.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Button
import androidx.tv.material3.Card
import androidx.tv.material3.Icon
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.MediaItem
import com.nauralta.augora.tv.data.formatTime
import com.nauralta.augora.tv.data.parseTimeSpan
import kotlinx.coroutines.launch

/** What happens when an item is chosen: folders open, playable items play, a long press opens the menu. */
class ItemActions(
    val onClick: (MediaItem, List<MediaItem>) -> Unit,
    val onLongClick: (MediaItem, List<MediaItem>) -> Unit,
)

/** Provides [ItemActions] to [content], and shows the menu of a long-pressed item. */
@Composable
fun WithItemActions(app: AugoraApp, onOpen: (MediaItem) -> Unit, content: @Composable (ItemActions) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val open by rememberUpdatedState(onOpen)
    var menu by remember { mutableStateOf<Pair<MediaItem, List<MediaItem>>?>(null) }

    val actions = remember {
        ItemActions(
            onClick = { item, siblings ->
                if (item.isBrowsable) open(item)
                else if (item.isPlayable) scope.launch { playItem(context, app, item, "Replace", siblings) }
            },
            onLongClick = { item, siblings -> menu = item to siblings },
        )
    }
    content(actions)

    menu?.let { (item, siblings) ->
        Dialog(onDismissRequest = { menu = null }) {
            Column(
                Modifier.clip(RoundedCornerShape(16.dp)).background(Surface).padding(24.dp).width(420.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(item.name, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (item.isBrowsable) {
                    MenuButton(S.open) { menu = null; open(item) }
                }
                if (item.isPlayable) {
                    MenuButton(S.playNow) {
                        menu = null
                        scope.launch { playItem(context, app, item, "Replace", siblings) }
                    }
                    MenuButton(S.playNext) {
                        menu = null
                        scope.launch { playItem(context, app, item, "Next") }
                    }
                    MenuButton(S.addToQueue) {
                        menu = null
                        scope.launch { playItem(context, app, item, "Add") }
                    }
                }
            }
        }
    }
}

@Composable
private fun MenuButton(text: String, onClick: () -> Unit) {
    Button(onClick = onClick, modifier = Modifier.fillMaxWidth()) { Text(text) }
}

/** The cover, or a note on a dark square while there is none. */
@Composable
fun Cover(url: String?, modifier: Modifier = Modifier) {
    Box(modifier.clip(RoundedCornerShape(8.dp)).background(SurfaceRaised), contentAlignment = Alignment.Center) {
        Icon(Icons.Note, contentDescription = null, tint = TextSecondary, modifier = Modifier.size(32.dp))
        if (url != null) {
            AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.matchParentSize())
        }
    }
}

/** A folder, album or station as a cover with its name. */
@Composable
fun MediaCard(item: MediaItem, imageUrl: String?, siblings: List<MediaItem>, actions: ItemActions, modifier: Modifier = Modifier) {
    Card(
        onClick = { actions.onClick(item, siblings) },
        onLongClick = { actions.onLongClick(item, siblings) },
        modifier = modifier,
    ) {
        Cover(imageUrl, Modifier.fillMaxWidth().aspectRatio(1f))
        Column(Modifier.padding(12.dp)) {
            Text(item.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
            Text(
                item.subtitle.orEmpty(),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                color = TextSecondary,
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/** A track or episode as a row. */
@Composable
fun MediaRowCard(item: MediaItem, imageUrl: String?, siblings: List<MediaItem>, actions: ItemActions, modifier: Modifier = Modifier) {
    Card(
        onClick = { actions.onClick(item, siblings) },
        onLongClick = { actions.onLongClick(item, siblings) },
        modifier = modifier.fillMaxWidth(),
    ) {
        Row(Modifier.padding(10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Cover(imageUrl, Modifier.size(56.dp))
            Column(Modifier.weight(1f)) {
                Text(item.name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                Text(
                    item.subtitle.orEmpty(),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    color = TextSecondary,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            parseTimeSpan(item.duration)?.let { Text(formatTime(it), color = TextSecondary) }
        }
    }
}
