package com.nauralta.augora.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.OutlinedTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.MediaItem
import com.nauralta.augora.tv.data.ProviderSearch
import kotlinx.coroutines.launch
import java.io.IOException

/** Search over all sources; the results are grouped by source. */
@Composable
fun SearchScreen(app: AugoraApp, onOpen: (MediaItem) -> Unit) {
    var query by remember { mutableStateOf("") }
    var results by remember { mutableStateOf<List<ProviderSearch>?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun search() {
        val text = query.trim()
        if (text.isEmpty()) return
        scope.launch {
            message = S.loading
            try {
                results = app.api.search(text).providers.filter { it.items.isNotEmpty() }
                message = if (results.isNullOrEmpty()) S.noResults else null
            } catch (e: IOException) {
                results = null
                message = e.message
            }
        }
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 48.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(S.search, style = MaterialTheme.typography.headlineMedium)
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            label = { Text(S.searchHint) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { search() }),
            modifier = Modifier.fillMaxWidth(),
        )
        message?.let { Text(it) }
        WithItemActions(app, onOpen) { actions ->
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(bottom = 24.dp)) {
                results?.forEach { provider ->
                    item(key = "header:${provider.provider}") {
                        Text(provider.providerName, style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(top = 12.dp))
                    }
                    items(provider.items, key = { "${provider.provider}:${it.uri}" }) { item ->
                        MediaRowCard(item, app.api.resolve(item.imageUrl), provider.items, actions)
                    }
                }
            }
        }
    }
}
