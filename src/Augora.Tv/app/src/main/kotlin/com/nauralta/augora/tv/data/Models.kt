package com.nauralta.augora.tv.data

import kotlinx.serialization.Serializable

// The JSON of the Augora API (Contracts). Enums stay strings and TimeSpans stay "hh:mm:ss" strings;
// the helpers below read them. Everything the server may leave out is nullable or has a default.

@Serializable
data class ExternalProvider(val scheme: String, val displayName: String)

@Serializable
data class AuthStatus(
    val setupRequired: Boolean = false,
    val externalProviders: List<ExternalProvider> = emptyList(),
)

@Serializable
data class AccessTokenResponse(
    val accessToken: String,
    val expiresIn: Long,
    val refreshToken: String,
)

@Serializable
data class MediaItem(
    val uri: String,
    val type: String = "Track",
    val name: String,
    val subtitle: String? = null,
    val imageUrl: String? = null,
    val isPlayable: Boolean = false,
    val isBrowsable: Boolean = false,
    val provider: String = "",
    val duration: String? = null,
) {
    /** A list of these reads better as rows than as a grid of covers. */
    val isTrackLike: Boolean get() = type == "Track" || type == "Episode"
}

@Serializable
data class BrowseResult(val uri: String? = null, val title: String = "", val items: List<MediaItem> = emptyList())

@Serializable
data class ProviderSearchError(val provider: String, val providerName: String, val error: String)

/** [items] holds the results of all sources, best match first. */
@Serializable
data class SearchResult(
    val query: String = "",
    val items: List<MediaItem> = emptyList(),
    val errors: List<ProviderSearchError> = emptyList(),
)

@Serializable
data class NowPlaying(
    val itemUri: String? = null,
    val title: String,
    val subtitle: String? = null,
    val imageUrl: String? = null,
    val isLive: Boolean = false,
    val position: String? = null,
    val duration: String? = null,
)

@Serializable
data class Player(
    val id: String,
    val name: String,
    val kind: String = "Speaker",
    val available: Boolean = true,
    val state: String = "Idle",
    val volume: Double = 1.0,
    val muted: Boolean = false,
    val features: String = "None",
    val nowPlaying: NowPlaying? = null,
) {
    val isPlaying: Boolean get() = state == "Playing" || state == "Buffering"
    val canSeek: Boolean get() = features.contains("Seek")
    val canSetVolume: Boolean get() = features.contains("Volume")
}

@Serializable
data class QueueItem(val id: String, val item: MediaItem)

@Serializable
data class Queue(
    val playerId: String,
    val items: List<QueueItem> = emptyList(),
    val currentIndex: Int? = null,
    val shuffle: Boolean = false,
    val repeat: String = "Off",
    val autoplay: Boolean = false,
)

// What this device exchanges with the server as a player (the remote player protocol).

@Serializable
data class RemoteMedia(
    val streamUrl: String,
    val contentType: String = "",
    val isLive: Boolean = false,
    val itemUri: String = "",
    val title: String = "",
    val subtitle: String? = null,
    val imageUrl: String? = null,
    val durationSeconds: Double? = null,
)

@Serializable
data class RemoteCommand(
    val type: String,
    val media: RemoteMedia? = null,
    val volume: Double? = null,
    val muted: Boolean? = null,
    val positionSeconds: Double? = null,
)

/** Parses the server's TimeSpan notation, e.g. <c>00:03:25.5000000</c> or <c>1.02:03:04</c>; seconds, or null. */
fun parseTimeSpan(value: String?): Double? {
    if (value.isNullOrBlank()) return null
    var text = value
    var days = 0.0
    val dot = text.indexOf('.')
    val colon = text.indexOf(':')
    if (dot in 0 until colon) {
        days = text.substring(0, dot).toDoubleOrNull() ?: return null
        text = text.substring(dot + 1)
    }
    val parts = text.split(':')
    if (parts.size != 3) return null
    val hours = parts[0].toDoubleOrNull() ?: return null
    val minutes = parts[1].toDoubleOrNull() ?: return null
    val seconds = parts[2].toDoubleOrNull() ?: return null
    return days * 86400 + hours * 3600 + minutes * 60 + seconds
}

fun formatTime(seconds: Double): String {
    val total = seconds.toLong().coerceAtLeast(0)
    val h = total / 3600
    val m = (total % 3600) / 60
    val s = total % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
}
