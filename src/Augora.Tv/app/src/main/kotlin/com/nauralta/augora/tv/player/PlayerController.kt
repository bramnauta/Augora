package com.nauralta.augora.tv.player

import android.os.Handler
import android.os.Looper
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import com.google.gson.JsonElement
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.nauralta.augora.tv.data.Api
import com.nauralta.augora.tv.data.RemoteCommand
import com.nauralta.augora.tv.data.RemoteMedia
import com.nauralta.augora.tv.data.Session
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.decodeFromString

private class RegisterRequest(val deviceId: String, val name: String, val kind: String, val ephemeral: Boolean = false)

private class StateReport(
    val state: String,
    val volume: Double,
    val muted: Boolean,
    val positionSeconds: Double?,
    val event: String = "None",
    val error: String? = null,
)

/**
 * Makes this TV an Augora player: registers over the events hub (the same protocol as the browser player and the
 * other apps), plays the commanded streams with ExoPlayer and reports its state.
 */
class PlayerController(
    private val session: Session,
    private val api: Api,
    private val exo: ExoPlayer,
) {
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var hub: HubConnection? = null
    private var playerId: String? = null

    // Only touched on the main thread.
    private var media: RemoteMedia? = null
    private var state = "Idle"
    private var volume = 1.0
    private var muted = false
    private var loaded = false
    private var awaitingReady = false

    /** Another app instance took over this device id; the owner should stop. */
    var onReleased: (() -> Unit)? = null

    /** What plays and in which state, for the notification. */
    var onNowPlaying: ((RemoteMedia?, String) -> Unit)? = null

    private val listener = object : Player.Listener {
        override fun onPlaybackStateChanged(playbackState: Int) {
            when (playbackState) {
                Player.STATE_READY -> if (awaitingReady) {
                    awaitingReady = false
                    exo.play()
                    setState("Playing")
                }
                Player.STATE_ENDED -> if (loaded) {
                    release()
                    setState("Idle", event = "Finished")
                }
            }
        }

        override fun onPlayerError(error: PlaybackException) {
            release()
            setState("Idle", event = "Error", error = "${error.errorCodeName}: ${error.message}")
        }

        override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
            // A call or another app took the audio: pause, and let the server know.
            val lost = reason == Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_FOCUS_LOSS ||
                reason == Player.PLAY_WHEN_READY_CHANGE_REASON_AUDIO_BECOMING_NOISY
            if (lost && state == "Playing") sendCommand("pause")
        }
    }

    fun start() {
        main.post { exo.addListener(listener) }
        scope.launch { connect() }
    }

    /** Called on the main thread, when the service ends. */
    fun stop() {
        scope.cancel()
        exo.removeListener(listener)
        release()
        val connection = hub
        hub = null
        if (connection != null) {
            CoroutineScope(Dispatchers.IO).launch { runCatching { connection.stop().blockingAwait() } }
        }
    }

    /** Sends a player command (pause, resume, next, previous, stop) through the server, so the queue follows. */
    fun sendCommand(command: String) {
        val id = playerId ?: return
        scope.launch {
            runCatching {
                when (command) {
                    "pause" -> api.pause(id)
                    "resume" -> api.resume(id)
                    "next" -> api.next(id)
                    "previous" -> api.previous(id)
                    "stop" -> api.stop(id)
                }
            }
        }
    }

    private suspend fun connect() {
        val connection = HubConnectionBuilder.create("${session.serverUrl}/hubs/events")
            .withAccessTokenProvider(Single.defer { Single.just(runBlocking { api.accessToken() } ?: "") })
            .build()
        hub = connection
        connection.on("PlayerCommand", { element: JsonElement ->
            val command = api.json.decodeFromString<RemoteCommand>(element.toString())
            main.post { execute(command) }
        }, JsonElement::class.java)
        connection.on("PlayerReleased", {
            main.post {
                release()
                onReleased?.invoke()
            }
        })

        while (scope.isActive) {
            try {
                connection.start().blockingAwait()
                register(connection)
                connection.onClosed { scope.launch { reconnect() } }
                return
            } catch (e: Exception) {
                if (!session.hasTokens) {
                    main.post { onReleased?.invoke() }
                    return
                }
                delay(10_000)
            }
        }
    }

    private suspend fun reconnect() {
        delay(3_000)
        if (scope.isActive) connect()
    }

    private fun register(connection: HubConnection) {
        playerId = connection.invoke(
            String::class.java, "RegisterPlayer", RegisterRequest(session.deviceId, session.deviceName, "Tv")
        ).blockingGet()
        main.post { report() }
    }

    private fun execute(command: RemoteCommand) {
        when (command.type) {
            "Play" -> command.media?.let { play(it) }
            "Pause" -> if (loaded && state == "Playing") {
                exo.pause()
                setState("Paused")
            }
            "Resume" -> if (loaded && state == "Paused") {
                exo.play()
                setState("Playing")
            }
            "Stop" -> {
                release()
                setState("Idle")
            }
            "SetVolume" -> command.volume?.let {
                volume = it.coerceIn(0.0, 1.0)
                applyVolume()
                report()
            }
            "SetMuted" -> command.muted?.let {
                muted = it
                applyVolume()
                report()
            }
            "Seek" -> if (loaded) command.positionSeconds?.let {
                exo.seekTo((maxOf(0.0, it) * 1000).toLong())
                report()
            }
        }
    }

    private fun play(item: RemoteMedia) {
        release()
        media = item
        val url = if (item.streamUrl.startsWith("/")) session.serverUrl + item.streamUrl else item.streamUrl
        exo.setMediaItem(MediaItem.fromUri(url))
        exo.playWhenReady = false
        exo.prepare()
        loaded = true
        awaitingReady = true
        applyVolume()
        setState("Buffering")
    }

    private fun release() {
        loaded = false
        awaitingReady = false
        exo.stop()
        exo.clearMediaItems()
    }

    private fun setState(newState: String, event: String = "None", error: String? = null) {
        state = newState
        onNowPlaying?.invoke(media, newState)
        report(event, error)
        if (newState == "Playing") schedulePositionReports()
    }

    /** Position updates while playing, so the UI's progress bar follows. */
    private fun schedulePositionReports() {
        main.postDelayed({
            if (state == "Playing") {
                report()
                schedulePositionReports()
            }
        }, 5_000)
    }

    private fun applyVolume() {
        exo.volume = if (muted) 0f else volume.toFloat()
    }

    private fun report(event: String = "None", error: String? = null) {
        val connection = hub ?: return
        if (playerId == null) return
        val position = if (loaded && (state == "Playing" || state == "Paused")) exo.currentPosition / 1000.0 else null
        val snapshot = StateReport(state, volume, muted, position, event, error)
        scope.launch {
            runCatching { connection.send("ReportPlayerState", snapshot) }
        }
    }
}
