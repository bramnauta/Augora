package com.nauralta.augora.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.BrowseResult
import com.nauralta.augora.tv.data.MediaItem
import java.io.IOException

private sealed interface Loaded {
    data object Loading : Loaded
    data class Done(val result: BrowseResult) : Loaded
    data class Failed(val message: String) : Loaded
}

/** A folder of the library, or its root (the sources) when [uri] is null. */
@Composable
fun BrowseScreen(app: AugoraApp, uri: String?, title: String, onOpen: (MediaItem) -> Unit) {
    var reload by remember { mutableIntStateOf(0) }
    val loaded by produceState<Loaded>(Loaded.Loading, uri, reload) {
        value = Loaded.Loading
        value = try {
            Loaded.Done(app.api.browse(uri))
        } catch (e: IOException) {
            Loaded.Failed(e.message.orEmpty())
        }
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 48.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(if (uri == null) S.sources else title, style = MaterialTheme.typography.headlineMedium)
        when (val state = loaded) {
            Loaded.Loading -> Text(S.loading)
            is Loaded.Failed -> {
                Text(state.message)
                Button(onClick = { reload++ }) { Text(S.retry) }
            }
            is Loaded.Done -> WithItemActions(app, onOpen) { actions ->
                val items = state.result.items
                if (items.isNotEmpty() && items.count { it.isTrackLike } * 2 >= items.size) {
                    LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
                        items(items, key = { it.uri }) { item ->
                            MediaRowCard(item, app.api.resolve(item.imageUrl), items, actions)
                        }
                    }
                } else {
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(190.dp),
                        horizontalArrangement = Arrangement.spacedBy(16.dp),
                        verticalArrangement = Arrangement.spacedBy(16.dp),
                        contentPadding = PaddingValues(bottom = 24.dp),
                    ) {
                        items(items, key = { it.uri }) { item ->
                            MediaCard(item, app.api.resolve(item.imageUrl), items, actions)
                        }
                    }
                }
            }
        }
    }
}
