package com.nauralta.augora.tv.data

import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The JSON below is what the server's System.Text.Json writes for the contracts. */
class ModelsTest {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; explicitNulls = false }

    @Test
    fun timeSpansAreRead() {
        assertEquals(205.5, parseTimeSpan("00:03:25.5000000")!!, 0.001)
        assertEquals(3723.0, parseTimeSpan("01:02:03")!!, 0.001)
        assertEquals(90062.0, parseTimeSpan("1.01:01:02")!!, 0.001)
        assertNull(parseTimeSpan(null))
        assertNull(parseTimeSpan("soon"))
    }

    @Test
    fun timesAreFormatted() {
        assertEquals("3:25", formatTime(205.5))
        assertEquals("1:02:03", formatTime(3723.0))
    }

    @Test
    fun aPlayerWithFlagsAndNowPlayingIsRead() {
        val player = json.decodeFromString<Player>(
            """
            {"id":"remote:abc","name":"Living room TV","provider":"remote","kind":"Tv","available":true,"state":"Playing",
             "volume":0.5,"muted":false,"features":"Volume, Mute, Pause, Seek",
             "nowPlaying":{"itemUri":"local:track:1","title":"Song","subtitle":"Artist","imageUrl":"/api/local/artwork/x","isLive":false,
                           "position":"00:01:00","duration":"00:03:25.1230000"},
             "externalSource":null,"model":null}
            """.trimIndent()
        )
        assertTrue(player.isPlaying)
        assertTrue(player.canSeek)
        assertTrue(player.canSetVolume)
        assertEquals("Song", player.nowPlaying?.title)
        assertEquals(60.0, parseTimeSpan(player.nowPlaying?.position)!!, 0.001)
    }

    @Test
    fun aPlayerWithoutFeaturesHasNone() {
        val player = json.decodeFromString<Player>(
            """{"id":"cast:1","name":"Kitchen","provider":"cast","kind":"Speaker","available":false,"state":"Idle","volume":1,"muted":false,"features":"None"}"""
        )
        assertTrue(!player.isPlaying && !player.canSeek && !player.canSetVolume)
        assertNull(player.nowPlaying)
    }

    @Test
    fun aQueueAndBrowseResultAreRead() {
        val queue = json.decodeFromString<Queue>(
            """
            {"playerId":"remote:abc","currentIndex":0,"shuffle":false,"repeat":"Off","autoplay":true,"items":[
              {"id":"q1","item":{"uri":"local:track:1","type":"Track","name":"Song","subtitle":null,"imageUrl":null,
                                 "isPlayable":true,"isBrowsable":false,"provider":"local","duration":"00:03:25"}}]}
            """.trimIndent()
        )
        assertEquals(1, queue.items.size)
        assertTrue(queue.autoplay)
        assertTrue(queue.items[0].item.isTrackLike)

        val browse = json.decodeFromString<BrowseResult>(
            """{"uri":null,"title":"Library","items":[{"uri":"tunein:","type":"Folder","name":"TuneIn","isPlayable":false,"isBrowsable":true,"provider":"tunein"}]}"""
        )
        assertEquals("TuneIn", browse.items[0].name)
        assertTrue(browse.items[0].isBrowsable)
    }

    @Test
    fun aRemoteCommandIsRead() {
        val command = json.decodeFromString<RemoteCommand>(
            """{"type":"Play","media":{"streamUrl":"/api/stream/abc","contentType":"audio/mpeg","isLive":true,"itemUri":"tunein:s1","title":"Radio","subtitle":null,"imageUrl":null,"durationSeconds":null},"volume":null,"muted":null,"positionSeconds":null}"""
        )
        assertEquals("Play", command.type)
        assertEquals("/api/stream/abc", command.media?.streamUrl)
        assertTrue(command.media!!.isLive)
    }
}
