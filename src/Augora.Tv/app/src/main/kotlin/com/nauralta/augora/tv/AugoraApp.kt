package com.nauralta.augora.tv

import android.app.Application
import coil3.ImageLoader
import coil3.SingletonImageLoader
import coil3.network.okhttp.OkHttpNetworkFetcherFactory
import com.nauralta.augora.tv.data.Api
import com.nauralta.augora.tv.data.Live
import com.nauralta.augora.tv.data.Session
import com.nauralta.augora.tv.player.PlayerService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The app's singletons: who is logged in on which server, the API and the live state. */
class AugoraApp : Application(), SingletonImageLoader.Factory {
    lateinit var session: Session
        private set
    lateinit var api: Api
        private set
    lateinit var live: Live
        private set

    private val _loggedIn = MutableStateFlow(false)
    val loggedIn: StateFlow<Boolean> = _loggedIn

    override fun onCreate() {
        super.onCreate()
        session = Session(this)
        api = Api(session)
        live = Live(session, api)
        api.onUnauthorized = { logout() }
        if (session.serverUrl != null && session.hasTokens) startSession()
    }

    /** After logging in: this TV becomes a player and the live state starts. */
    fun startSession() {
        _loggedIn.value = true
        PlayerService.start(this)
        live.start()
    }

    fun logout() {
        PlayerService.stop(this)
        live.stop()
        session.clearTokens()
        _loggedIn.value = false
    }

    override fun newImageLoader(context: coil3.PlatformContext): ImageLoader =
        ImageLoader.Builder(context).components { add(OkHttpNetworkFetcherFactory()) }.build()
}
