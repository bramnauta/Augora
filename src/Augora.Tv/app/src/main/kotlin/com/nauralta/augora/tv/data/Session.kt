package com.nauralta.augora.tv.data

import android.content.Context
import android.provider.Settings
import java.util.UUID

/** The server address, device id and tokens, in the app's private preferences. */
class Session(context: Context) {
    private val prefs = context.getSharedPreferences("augora", Context.MODE_PRIVATE)
    private val resolver = context.contentResolver

    /** Without trailing slash, e.g. `http://192.168.1.10:4727`. */
    var serverUrl: String?
        get() = prefs.getString("server", null)
        set(value) = prefs.edit().putString("server", value?.trimEnd('/')).apply()

    /** Stable per installation, so the player keeps its id and queue. */
    val deviceId: String
        get() = prefs.getString("device", null) ?: UUID.randomUUID().toString().replace("-", "").also {
            prefs.edit().putString("device", it).apply()
        }

    val deviceName: String
        get() = Settings.Global.getString(resolver, "device_name")?.takeIf { it.isNotEmpty() }
            ?: "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}"

    /** The id the server gives this device's own player. */
    val devicePlayerId: String get() = "remote:$deviceId"

    val refreshToken: String? get() = prefs.getString("refresh", null)
    val accessToken: String? get() = prefs.getString("access", null)
    private val accessExpires: Long get() = prefs.getLong("access_expires", 0)

    val hasTokens: Boolean get() = !refreshToken.isNullOrEmpty()

    fun accessTokenValid(): Boolean = accessToken != null && accessExpires > System.currentTimeMillis() + 60_000

    fun saveTokens(tokens: AccessTokenResponse) {
        prefs.edit()
            .putString("access", tokens.accessToken)
            .putLong("access_expires", System.currentTimeMillis() + tokens.expiresIn * 1000)
            .putString("refresh", tokens.refreshToken)
            .apply()
    }

    fun clearTokens() {
        prefs.edit().remove("access").remove("access_expires").remove("refresh").apply()
    }
}
