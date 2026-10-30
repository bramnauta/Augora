package com.nauralta.augora.tv.ui

import android.content.Context
import android.widget.Toast
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.ApiException
import com.nauralta.augora.tv.data.MediaItem
import java.io.IOException

/** Plays or queues [item] on the selected player, and says what happened. */
suspend fun playItem(
    context: Context,
    app: AugoraApp,
    item: MediaItem,
    option: String = "Replace",
    siblings: List<MediaItem>? = null,
) {
    val playerId = app.live.selectedId.value
    val playerName = app.live.player(playerId)?.name.orEmpty()

    // "Play from here": a track played from an album or playlist brings the tracks after it, so playback continues.
    val following = if (option == "Replace" && item.isTrackLike && siblings != null) {
        siblings.dropWhile { it.uri != item.uri }.drop(1).filter { it.isPlayable && !it.isBrowsable }.map { it.uri }
    } else {
        null
    }

    val message = try {
        app.api.play(playerId, item.uri, option, following)
        if (option == "Replace" || option == "Play") S.playingOn(playerName) else S.addedTo(playerName)
    } catch (e: ApiException) {
        if (e.code == "spotify_pairing_required") S.spotifyPairing else e.message.orEmpty()
    } catch (e: IOException) {
        e.message.orEmpty()
    }
    Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
}
