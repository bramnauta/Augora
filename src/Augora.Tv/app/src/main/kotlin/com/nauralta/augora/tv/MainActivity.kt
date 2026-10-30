package com.nauralta.augora.tv

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import com.nauralta.augora.tv.ui.AugoraTheme
import com.nauralta.augora.tv.ui.LoginScreen
import com.nauralta.augora.tv.ui.MainScreen

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        val app = application as AugoraApp
        setContent {
            AugoraTheme {
                val loggedIn by app.loggedIn.collectAsState()
                if (loggedIn) MainScreen(app) else LoginScreen(app)
            }
        }
    }
}
