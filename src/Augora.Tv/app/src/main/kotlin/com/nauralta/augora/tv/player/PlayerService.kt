package com.nauralta.augora.tv.player

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.Player
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.MediaSession
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.MainActivity
import com.nauralta.augora.tv.R
import com.nauralta.augora.tv.data.RemoteMedia
import okhttp3.OkHttpClient

/**
 * Keeps this TV a player while the app is in the background: a foreground service around ExoPlayer and a media
 * session, so the remote's play/pause and next/previous keys work. The commands go through the server (see
 * [PlayerController]), so the queue and the other apps follow.
 */
class PlayerService : Service() {
    private lateinit var exo: ExoPlayer
    private lateinit var session: MediaSession
    private lateinit var controller: PlayerController
    private var media: RemoteMedia? = null
    private var state = "Idle"

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val app = application as AugoraApp

        val channel = NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)

        exo = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(OkHttpDataSource.Factory(OkHttpClient())))
            .setAudioAttributes(
                AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(),
                true,
            )
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .setLoadControl(DefaultLoadControl())
            .build()

        controller = PlayerController(app.session, app.api, exo)
        controller.onNowPlaying = { item, newState ->
            media = item
            state = newState
            getSystemService(NotificationManager::class.java).notify(NOTIFICATION, buildNotification())
        }
        controller.onReleased = { stopSelf() }

        session = MediaSession.Builder(this, RemoteKeysPlayer(exo, controller)).build()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIFICATION, buildNotification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
        controller.start()
        return START_STICKY
    }

    override fun onDestroy() {
        controller.stop()
        session.release()
        exo.release()
        super.onDestroy()
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle(media?.title ?: getString(R.string.app_name))
            .setContentText(media?.subtitle ?: getString(R.string.player_idle))
            .setContentIntent(open)
            .setOngoing(state == "Playing" || state == "Buffering")
            .build()
    }

    /** The remote's media keys: they go through the server, like in the other apps. */
    private class RemoteKeysPlayer(player: Player, private val controller: PlayerController) : ForwardingPlayer(player) {
        override fun play() = controller.sendCommand("resume")

        override fun pause() = controller.sendCommand("pause")

        override fun stop() = controller.sendCommand("stop")

        override fun seekToNext() = controller.sendCommand("next")

        override fun seekToNextMediaItem() = controller.sendCommand("next")

        override fun seekToPrevious() = controller.sendCommand("previous")

        override fun seekToPreviousMediaItem() = controller.sendCommand("previous")

        override fun getAvailableCommands(): Player.Commands =
            super.getAvailableCommands().buildUpon()
                .addAll(
                    Player.COMMAND_SEEK_TO_NEXT,
                    Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM,
                    Player.COMMAND_SEEK_TO_PREVIOUS,
                    Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM,
                )
                .build()
    }

    companion object {
        private const val CHANNEL = "playback"
        private const val NOTIFICATION = 1

        fun start(context: Context) = ContextCompat.startForegroundService(context, Intent(context, PlayerService::class.java))

        fun stop(context: Context) {
            context.stopService(Intent(context, PlayerService::class.java))
        }
    }
}
