package com.nauralta.augora.tv.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.OutlinedTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.nauralta.augora.tv.AugoraApp
import com.nauralta.augora.tv.data.ApiException
import kotlinx.coroutines.launch
import java.io.IOException

/** Asks for the server and a user name and password. */
@Composable
fun LoginScreen(app: AugoraApp) {
    var server by remember { mutableStateOf(app.session.serverUrl.orEmpty()) }
    var userName by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    fun logIn() {
        if (busy) return
        busy = true
        message = null
        scope.launch {
            try {
                var url = server.trim().trimEnd('/')
                if (!url.startsWith("http://", true) && !url.startsWith("https://", true)) url = "http://$url"
                val status = app.api.status(url)
                if (status.setupRequired) {
                    message = S.setupRequired
                    return@launch
                }
                if (url != app.session.serverUrl) app.session.clearTokens()
                app.session.serverUrl = url
                if (app.api.login(userName.trim(), password)) {
                    app.startSession()
                } else {
                    message = if (status.externalProviders.isNotEmpty() && password.isEmpty()) S.oidcHint else S.wrongCredentials
                }
            } catch (e: IOException) {
                message = S.cannotReach(if (e is ApiException) e.message.orEmpty() else e.message.orEmpty())
            } catch (e: IllegalArgumentException) {
                message = S.cannotReach(e.message.orEmpty())
            } finally {
                busy = false
            }
        }
    }

    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            Modifier.width(520.dp).padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(S.connectTitle, style = MaterialTheme.typography.headlineMedium)
            OutlinedTextField(
                value = server,
                onValueChange = { server = it },
                label = { Text(S.serverHint) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
                modifier = Modifier.width(456.dp),
            )
            OutlinedTextField(
                value = userName,
                onValueChange = { userName = it },
                label = { Text(S.userName) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
                modifier = Modifier.width(456.dp),
            )
            OutlinedTextField(
                value = password,
                onValueChange = { password = it },
                label = { Text(S.password) },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(onGo = { logIn() }),
                modifier = Modifier.width(456.dp),
            )
            Button(onClick = { logIn() }, enabled = !busy) { Text(S.logIn) }
            message?.let { Text(it, color = Color(0xFFFF6B6B)) }
        }
    }
}
