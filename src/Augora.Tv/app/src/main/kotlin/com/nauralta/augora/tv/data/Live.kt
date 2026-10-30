package com.nauralta.augora.tv.data

import com.google.gson.JsonElement
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.decodeFromString

/**
 * What the screens show: all players, the selected player and its queue, kept current through the events hub.
 * The app starts on its own player, so music plays on the TV itself unless another player is picked.
 */
class Live(private val session: Session, private val api: Api) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null

    private val _players = MutableStateFlow<Map<String, Player>>(emptyMap())
    private val _selectedId = MutableStateFlow(session.devicePlayerId)
    private val _queue = MutableStateFlow<Queue?>(null)
    private val _connected = MutableStateFlow(false)

    val players: StateFlow<Map<String, Player>> = _players
    val selectedId: StateFlow<String> = _selectedId
    val queue: StateFlow<Queue?> = _queue
    val connected: StateFlow<Boolean> = _connected

    fun start() {
        if (job != null) return
        _selectedId.value = session.devicePlayerId
        job = scope.launch { run() }
    }

    fun stop() {
        job?.cancel()
        job = null
        _connected.value = false
        _players.value = emptyMap()
        _queue.value = null
    }

    suspend fun select(playerId: String) {
        _selectedId.value = playerId
        _queue.value = null
        refreshQueue()
    }

    suspend fun refreshQueue() {
        _queue.value = try {
            api.queue(_selectedId.value)
        } catch (e: ApiException) {
            // The own player is not registered yet; the queue arrives with its first update.
            null
        }
    }

    private suspend fun run() {
        while (scope.isActive) {
            val closed = CompletableDeferred<Unit>()
            val hub = HubConnectionBuilder.create("${session.serverUrl}/hubs/events")
                .withAccessTokenProvider(Single.defer { Single.just(runBlocking { api.accessToken() } ?: "") })
                .build()
            hub.on("PlayerUpdated", { element: JsonElement ->
                val player = api.json.decodeFromString<Player>(element.toString())
                _players.update { it + (player.id to player) }
            }, JsonElement::class.java)
            hub.on("PlayerRemoved", { id: String -> _players.update { it - id } }, String::class.java)
            hub.on("QueueUpdated", { element: JsonElement ->
                val queue = api.json.decodeFromString<Queue>(element.toString())
                if (queue.playerId.equals(_selectedId.value, ignoreCase = true)) _queue.value = queue
            }, JsonElement::class.java)
            hub.onClosed { closed.complete(Unit) }

            try {
                hub.start().blockingAwait()
                _connected.value = true
                _players.value = api.players().associateBy { it.id }
                refreshQueue()
                closed.await()
            } catch (e: Exception) {
                // Server unreachable for a moment; try again.
            } finally {
                _connected.value = false
                runCatching { hub.stop().blockingAwait() }
            }

            if (!session.hasTokens) return
            delay(3000)
        }
    }

    fun player(id: String): Player? = _players.value[id]
}
