package com.nauralta.augora.tv.ui

import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.darkColorScheme

val Accent = Color(0xFFB388FF)
val OnAccent = Color(0xFF1A0B33)
val Background = Color(0xFF121212)
val Surface = Color(0xFF1E1E1E)
val SurfaceRaised = Color(0xFF2A2A2A)
val TextSecondary = Color(0xFFB3B3B3)

@Composable
fun AugoraTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Accent,
            onPrimary = OnAccent,
            secondary = Accent,
            background = Background,
            onBackground = Color.White,
            surface = Surface,
            onSurface = Color.White,
            surfaceVariant = SurfaceRaised,
            onSurfaceVariant = TextSecondary,
        ),
        content = content,
    )
}
