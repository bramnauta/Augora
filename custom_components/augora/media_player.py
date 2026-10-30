"""Augora players as Home Assistant media players."""

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Any

import voluptuous as vol

from homeassistant.components import media_source
from homeassistant.components.media_player import (
    BrowseError,
    BrowseMedia,
    MediaClass,
    MediaPlayerEnqueue,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
    MediaPlayerState,
    MediaType,
    RepeatMode,
)
from homeassistant.components.media_player.browse_media import async_process_play_media_url
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import config_validation as cv, entity_platform
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.dispatcher import async_dispatcher_connect
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.util import dt as dt_util

from . import AugoraConfigEntry, AugoraHub
from .api import AugoraError
from .const import (
    ATTR_AUTOPLAY,
    ATTR_MEDIA_ID,
    ATTR_RESUME,
    ATTR_VOLUME_LEVEL,
    DOMAIN,
    ERROR_NO_FREE_SPOTIFY_ACCOUNT,
    SERVICE_ANNOUNCE,
    SIGNAL_NEW_PLAYER,
    SIGNAL_PLAYER_UPDATED,
    SIGNAL_QUEUE_UPDATED,
)

STATES = {
    "Idle": MediaPlayerState.IDLE,
    "Buffering": MediaPlayerState.BUFFERING,
    "Playing": MediaPlayerState.PLAYING,
    "Paused": MediaPlayerState.PAUSED,
}

MEDIA_CLASSES = {
    "Folder": MediaClass.DIRECTORY,
    "Radio": MediaClass.CHANNEL,
    "Track": MediaClass.TRACK,
    "Album": MediaClass.ALBUM,
    "Artist": MediaClass.ARTIST,
    "Playlist": MediaClass.PLAYLIST,
    "Podcast": MediaClass.PODCAST,
    "Episode": MediaClass.EPISODE,
    "Audiobook": MediaClass.PODCAST,
}

ENQUEUE = {
    MediaPlayerEnqueue.ADD: "Add",
    MediaPlayerEnqueue.NEXT: "Next",
    MediaPlayerEnqueue.PLAY: "Play",
    MediaPlayerEnqueue.REPLACE: "Replace",
}

REPEAT_TO_HA = {"Off": RepeatMode.OFF, "One": RepeatMode.ONE, "All": RepeatMode.ALL}
REPEAT_FROM_HA = {value: key for key, value in REPEAT_TO_HA.items()}


async def async_setup_entry(hass: HomeAssistant, entry: AugoraConfigEntry, async_add_entities: AddConfigEntryEntitiesCallback) -> None:
    hub = entry.runtime_data
    async_add_entities(AugoraMediaPlayer(hub, player_id) for player_id in hub.players)

    @callback
    def add_player(player_id: str) -> None:
        async_add_entities([AugoraMediaPlayer(hub, player_id)])

    entry.async_on_unload(async_dispatcher_connect(hass, SIGNAL_NEW_PLAYER.format(entry.entry_id), add_player))

    entity_platform.async_get_current_platform().async_register_entity_service(
        SERVICE_ANNOUNCE,
        {
            vol.Required(ATTR_MEDIA_ID): cv.string,
            vol.Optional(ATTR_VOLUME_LEVEL): vol.All(vol.Coerce(float), vol.Range(min=0, max=1)),
            vol.Optional(ATTR_RESUME, default=True): cv.boolean,
        },
        "async_announce",
    )


class AugoraMediaPlayer(MediaPlayerEntity):
    """One Augora player: a Cast device or group, a browser, a phone or a TV."""

    _attr_has_entity_name = True
    _attr_name = None
    _attr_should_poll = False
    _attr_media_content_type = MediaType.MUSIC

    def __init__(self, hub: AugoraHub, player_id: str) -> None:
        self._hub = hub
        self._player_id = player_id
        self._position_updated_at: datetime | None = None
        self._attr_unique_id = f"{hub.entry.entry_id}_{player_id}"
        player = self._player
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, f"{hub.entry.entry_id}_{player_id}")},
            name=player["name"],
            manufacturer=f"Augora ({player['provider']})",
            model=player.get("model") or player.get("kind"),
        )

    @property
    def _player(self) -> dict[str, Any]:
        return self._hub.players[self._player_id]

    @property
    def _queue(self) -> dict[str, Any] | None:
        return self._hub.queues.get(self._player_id)

    async def async_added_to_hass(self) -> None:
        self.async_on_remove(async_dispatcher_connect(self.hass, SIGNAL_PLAYER_UPDATED.format(self._player_id), self._updated))
        self.async_on_remove(async_dispatcher_connect(self.hass, SIGNAL_QUEUE_UPDATED.format(self._player_id), self._updated))
        try:
            self._hub.queues[self._player_id] = await self._hub.client.get_queue(self._player_id)
        except AugoraError:
            pass

    @callback
    def _updated(self) -> None:
        self._position_updated_at = dt_util.utcnow()
        self.async_write_ha_state()

    @property
    def available(self) -> bool:
        return self._hub.connected and bool(self._player.get("available"))

    @property
    def supported_features(self) -> MediaPlayerEntityFeature:
        features = (
            MediaPlayerEntityFeature.PLAY
            | MediaPlayerEntityFeature.STOP
            | MediaPlayerEntityFeature.NEXT_TRACK
            | MediaPlayerEntityFeature.PREVIOUS_TRACK
            | MediaPlayerEntityFeature.PLAY_MEDIA
            | MediaPlayerEntityFeature.BROWSE_MEDIA
            | MediaPlayerEntityFeature.MEDIA_ENQUEUE
            | MediaPlayerEntityFeature.SHUFFLE_SET
            | MediaPlayerEntityFeature.REPEAT_SET
            | MediaPlayerEntityFeature.MEDIA_ANNOUNCE
        )
        player_features = self._player.get("features") or ""
        if "Pause" in player_features:
            features |= MediaPlayerEntityFeature.PAUSE
        if "Volume" in player_features:
            features |= MediaPlayerEntityFeature.VOLUME_SET | MediaPlayerEntityFeature.VOLUME_STEP
        if "Mute" in player_features:
            features |= MediaPlayerEntityFeature.VOLUME_MUTE
        if "Seek" in player_features and not (self._now_playing or {}).get("isLive"):
            features |= MediaPlayerEntityFeature.SEEK
        return features

    @property
    def state(self) -> MediaPlayerState:
        return STATES.get(self._player.get("state", "Idle"), MediaPlayerState.IDLE)

    @property
    def volume_level(self) -> float | None:
        return self._player.get("volume")

    @property
    def is_volume_muted(self) -> bool | None:
        return self._player.get("muted")

    @property
    def _now_playing(self) -> dict[str, Any] | None:
        return self._player.get("nowPlaying")

    @property
    def media_content_id(self) -> str | None:
        return (self._now_playing or {}).get("itemUri")

    @property
    def media_title(self) -> str | None:
        return (self._now_playing or {}).get("title") or self._player.get("externalSource")

    @property
    def media_artist(self) -> str | None:
        return (self._now_playing or {}).get("subtitle")

    @property
    def media_image_url(self) -> str | None:
        return self._hub.client.absolute_url((self._now_playing or {}).get("imageUrl"))

    @property
    def media_image_remotely_accessible(self) -> bool:
        return False

    @property
    def media_duration(self) -> int | None:
        return _seconds((self._now_playing or {}).get("duration"))

    @property
    def media_position(self) -> int | None:
        return _seconds((self._now_playing or {}).get("position"))

    @property
    def media_position_updated_at(self) -> datetime | None:
        return self._position_updated_at if self._now_playing else None

    @property
    def shuffle(self) -> bool | None:
        return (self._queue or {}).get("shuffle")

    @property
    def repeat(self) -> RepeatMode | None:
        return REPEAT_TO_HA.get((self._queue or {}).get("repeat", "Off"))

    async def async_media_play(self) -> None:
        await self._call(self._hub.client.command(self._player_id, "resume"))

    async def async_media_pause(self) -> None:
        await self._call(self._hub.client.command(self._player_id, "pause"))

    async def async_media_stop(self) -> None:
        await self._call(self._hub.client.command(self._player_id, "stop"))

    async def async_media_next_track(self) -> None:
        await self._call(self._hub.client.command(self._player_id, "next"))

    async def async_media_previous_track(self) -> None:
        await self._call(self._hub.client.command(self._player_id, "previous"))

    async def async_media_seek(self, position: float) -> None:
        await self._call(self._hub.client.seek(self._player_id, position))

    async def async_set_volume_level(self, volume: float) -> None:
        await self._call(self._hub.client.set_volume(self._player_id, volume))

    async def async_mute_volume(self, mute: bool) -> None:
        await self._call(self._hub.client.set_muted(self._player_id, mute))

    async def async_set_shuffle(self, shuffle: bool) -> None:
        await self._call(self._hub.client.set_shuffle(self._player_id, shuffle))

    async def async_set_repeat(self, repeat: RepeatMode) -> None:
        await self._call(self._hub.client.set_repeat(self._player_id, REPEAT_FROM_HA[repeat]))

    async def async_play_media(self, media_type: str, media_id: str, enqueue: MediaPlayerEnqueue | None = None, **kwargs: Any) -> None:
        """media_id is an Augora uri, e.g. tunein:radio:s24939 or spotify:playlist:...

        extra.autoplay switches autoplay (track radio) on or off for the queue along with starting.

        With announce (tts.speak, assist) it is an announcement instead: a media source or url that plays once, after which
        the music continues. extra.volume sets a fixed volume for it.
        """
        if kwargs.get("announce"):
            volume = (kwargs.get("extra") or {}).get(ATTR_VOLUME_LEVEL)
            await self.async_announce(media_id, float(volume) if volume is not None else None)
            return

        option = ENQUEUE.get(enqueue, "Replace") if enqueue else "Replace"
        autoplay = (kwargs.get("extra") or {}).get(ATTR_AUTOPLAY)
        await self._call(self._hub.client.play(self._player_id, media_id, option, None if autoplay is None else bool(autoplay)))

    async def async_announce(self, media_id: str, volume_level: float | None = None, resume: bool = True) -> None:
        """Entity service augora.announce: plays media_id (url or media source, e.g. TTS) and returns when it has ended."""
        content_type = None
        if media_source.is_media_source_id(media_id):
            try:
                sourced = await media_source.async_resolve_media(self.hass, media_id, self.entity_id)
            except media_source.MediaSourceError as err:
                raise HomeAssistantError(f"Cannot resolve {media_id}: {err}") from err
            media_id, content_type = sourced.url, sourced.mime_type

        # Speakers fetch the file themselves: it needs the address of Home Assistant (and a signature) they can reach.
        url = async_process_play_media_url(self.hass, media_id)
        await self._call(self._hub.client.announce(self._player_id, url, volume_level, resume, content_type))

    async def async_browse_media(self, media_content_type: str | None = None, media_content_id: str | None = None) -> BrowseMedia:
        try:
            result = await self._hub.client.browse(media_content_id)
        except AugoraError as err:
            raise BrowseError(str(err)) from err

        children = [
            BrowseMedia(
                media_class=MEDIA_CLASSES.get(item["type"], MediaClass.MUSIC),
                media_content_id=item["uri"],
                media_content_type=MediaType.MUSIC,
                title=item["name"] if not item.get("subtitle") else f"{item['name']} – {item['subtitle']}",
                can_play=item.get("isPlayable", False),
                can_expand=item.get("isBrowsable", False),
                thumbnail=self._hub.client.absolute_url(item.get("imageUrl")),
            )
            for item in result.get("items", [])
        ]
        return BrowseMedia(
            media_class=MediaClass.DIRECTORY,
            media_content_id=result.get("uri") or "",
            media_content_type=MediaType.MUSIC,
            title=result.get("title") or "Augora",
            can_play=False,
            can_expand=True,
            children=children,
            children_media_class=MediaClass.DIRECTORY,
        )

    async def _call(self, request: Any) -> None:
        try:
            await request
        except AugoraError as err:
            if err.code == ERROR_NO_FREE_SPOTIFY_ACCOUNT:
                holders = ", ".join(f"{h.get('accountName')} → {h.get('playerName')}" for h in err.details.get("holders", []))
                raise HomeAssistantError(f"All Spotify accounts are in use ({holders})") from err
            raise HomeAssistantError(str(err)) from err


def _seconds(value: str | None) -> int | None:
    """TimeSpan as serialised by .NET: [d.]hh:mm:ss[.fffffff]."""
    if not value:
        return None
    days = 0
    if "." in value.split(":")[0]:
        day_part, value = value.split(".", 1)
        days = int(day_part)
    hours, minutes, seconds = value.split(":")
    return int(timedelta(days=days, hours=int(hours), minutes=int(minutes), seconds=float(seconds)).total_seconds())
