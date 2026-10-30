package com.nauralta.augora.tv.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

/** A failed call; [message] is the Problem Details title, [code] the machine readable `code`. */
class ApiException(message: String, val code: String?, val status: Int, val problem: JsonObject?) : IOException(message)

@Serializable private class LoginRequest(val userName: String, val password: String, val useCookies: Boolean = false)
@Serializable private class RefreshRequest(val refreshToken: String)
@Serializable private class PlayRequest(val uri: String, val option: String, val takeoverPlayerId: String? = null, val following: List<String>? = null)
@Serializable private class SeekRequest(val positionSeconds: Double)
@Serializable private class VolumeRequest(val level: Double)

/** The REST API of the server, with the bearer token of [session]. */
class Api(private val session: Session) {
    val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; explicitNulls = false }

    private val http = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()
    private val refreshLock = Mutex()
    private val jsonType = "application/json".toMediaType()

    /** The server refused the token: the login is gone. */
    var onUnauthorized: (() -> Unit)? = null

    /** Checks that an Augora server answers at [serverUrl], and how it wants users to log in. */
    suspend fun status(serverUrl: String): AuthStatus =
        json.decodeFromString(send("GET", "$serverUrl/api/auth/status", null, authorized = false))

    /** Logs in with a password; false when the user name or password is wrong. */
    suspend fun login(userName: String, password: String): Boolean = try {
        val body = send("POST", url("api/auth/login"), json.encodeToString(LoginRequest(userName, password)), authorized = false)
        session.saveTokens(json.decodeFromString(body))
        true
    } catch (e: ApiException) {
        if (e.status == 401) false else throw e
    }

    /** A valid access token, refreshed when needed; null when the login is gone. */
    suspend fun accessToken(): String? {
        if (session.accessTokenValid()) return session.accessToken
        return refreshLock.withLock {
            if (session.accessTokenValid()) return session.accessToken
            val refresh = session.refreshToken ?: return null
            try {
                val body = send("POST", url("api/auth/refresh"), json.encodeToString(RefreshRequest(refresh)), authorized = false)
                session.saveTokens(json.decodeFromString(body))
                session.accessToken
            } catch (e: ApiException) {
                if (e.status == 401 || e.status == 403) {
                    session.clearTokens()
                    null
                } else {
                    throw e
                }
            }
        }
    }

    suspend fun browse(uri: String?): BrowseResult =
        json.decodeFromString(get(if (uri == null) "api/browse" else "api/browse?uri=${enc(uri)}"))

    suspend fun search(query: String): SearchResult = json.decodeFromString(get("api/search?q=${enc(query)}"))

    suspend fun players(): List<Player> = json.decodeFromString(get("api/players"))

    suspend fun queue(playerId: String): Queue = json.decodeFromString(get("api/players/${enc(playerId)}/queue"))

    suspend fun play(playerId: String, uri: String, option: String = "Replace", following: List<String>? = null) {
        post("api/players/${enc(playerId)}/play", json.encodeToString(PlayRequest(uri, option, following = following)))
    }

    suspend fun pause(playerId: String) = post("api/players/${enc(playerId)}/pause")

    suspend fun resume(playerId: String) = post("api/players/${enc(playerId)}/resume")

    suspend fun stop(playerId: String) = post("api/players/${enc(playerId)}/stop")

    suspend fun next(playerId: String) = post("api/players/${enc(playerId)}/next")

    suspend fun previous(playerId: String) = post("api/players/${enc(playerId)}/previous")

    suspend fun seek(playerId: String, seconds: Double) =
        post("api/players/${enc(playerId)}/seek", json.encodeToString(SeekRequest(seconds)))

    suspend fun setVolume(playerId: String, level: Double) {
        send("PUT", url("api/players/${enc(playerId)}/volume"), json.encodeToString(VolumeRequest(level)))
    }

    suspend fun playQueueItem(playerId: String, itemId: String) =
        post("api/players/${enc(playerId)}/queue/items/${enc(itemId)}/play")

    suspend fun removeQueueItem(playerId: String, itemId: String) {
        send("DELETE", url("api/players/${enc(playerId)}/queue/items/${enc(itemId)}"), null)
    }

    suspend fun clearQueue(playerId: String) {
        send("DELETE", url("api/players/${enc(playerId)}/queue"), null)
    }

    suspend fun setShuffle(playerId: String, enabled: Boolean) {
        send("PUT", url("api/players/${enc(playerId)}/queue/shuffle?enabled=$enabled"), null)
    }

    suspend fun setAutoplay(playerId: String, enabled: Boolean) {
        send("PUT", url("api/players/${enc(playerId)}/queue/autoplay?enabled=$enabled"), null)
    }

    suspend fun setRepeat(playerId: String, mode: String) {
        send("PUT", url("api/players/${enc(playerId)}/queue/repeat?mode=$mode"), null)
    }

    /** Artwork URLs from the server are relative to it. */
    fun resolve(url: String?): String? =
        if (url.isNullOrEmpty()) null else if (url.startsWith("/")) session.serverUrl + url else url

    private suspend fun get(path: String): String = send("GET", url(path), null)

    private suspend fun post(path: String, body: String? = null) {
        send("POST", url(path), body)
    }

    private fun url(path: String) = "${session.serverUrl}/$path"

    private fun enc(value: String) = URLEncoder.encode(value, "UTF-8").replace("+", "%20")

    private suspend fun send(method: String, url: String, body: String?, authorized: Boolean = true): String {
        val token = if (authorized) accessToken() else null
        return withContext(Dispatchers.IO) {
            val builder = Request.Builder().url(url)
            if (token != null) builder.header("Authorization", "Bearer $token")
            val requestBody = (body ?: if (method == "GET") null else "").let { it?.toRequestBody(jsonType) }
            builder.method(method, requestBody)
            http.newCall(builder.build()).execute().use { response ->
                val text = response.body?.string().orEmpty()
                if (!response.isSuccessful) {
                    if (response.code == 401 && authorized) onUnauthorized?.invoke()
                    throw problem(response.code, text)
                }
                text
            }
        }
    }

    private fun problem(status: Int, text: String): ApiException {
        val root = runCatching { json.parseToJsonElement(text).jsonObject }.getOrNull()
        val title = root?.get("title")?.jsonPrimitive?.contentOrNull ?: "HTTP $status"
        return ApiException(title, root?.get("code")?.jsonPrimitive?.contentOrNull, status, root)
    }
}
