"""The AudioCentral integration: every AudioCentral player becomes a media player entity."""

from __future__ import annotations

from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed, ConfigEntryNotReady
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.dispatcher import async_dispatcher_send

from .api import AudioCentralAuthError, AudioCentralClient, AudioCentralError, AudioCentralEvents
from .const import CONF_TOKEN, CONF_URL, SIGNAL_NEW_PLAYER, SIGNAL_PLAYER_UPDATED, SIGNAL_QUEUE_UPDATED

PLATFORMS = [Platform.MEDIA_PLAYER]

type AudioCentralConfigEntry = ConfigEntry[AudioCentralHub]


class AudioCentralHub:
    """The players of one AudioCentral server, kept current by the event hub."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry, client: AudioCentralClient) -> None:
        self.hass = hass
        self.entry = entry
        self.client = client
        self.players: dict[str, dict[str, Any]] = {}
        self.queues: dict[str, dict[str, Any]] = {}
        self.connected = False
        self.events = AudioCentralEvents(async_get_clientsession(hass), client, self._handle_event, self._on_connected)

    async def async_refresh(self) -> None:
        for player in await self.client.get_players():
            self._update_player(player)

    async def _on_connected(self) -> None:
        # Changes made while disconnected were missed: fetch everything once.
        self.connected = True
        await self.async_refresh()

    def _handle_event(self, target: str, arguments: list[Any]) -> None:
        if target == "PlayerUpdated" and arguments:
            self._update_player(arguments[0])
        elif target == "PlayerRemoved" and arguments:
            player = self.players.get(arguments[0])
            if player:
                player["available"] = False
                async_dispatcher_send(self.hass, SIGNAL_PLAYER_UPDATED.format(arguments[0]))
        elif target == "QueueUpdated" and arguments:
            queue = arguments[0]
            self.queues[queue["playerId"]] = queue
            async_dispatcher_send(self.hass, SIGNAL_QUEUE_UPDATED.format(queue["playerId"]))
        elif target == "Disconnected":
            self.connected = False
            for player_id in self.players:
                async_dispatcher_send(self.hass, SIGNAL_PLAYER_UPDATED.format(player_id))

    def _update_player(self, player: dict[str, Any]) -> None:
        is_new = player["id"] not in self.players
        self.players[player["id"]] = player
        if is_new:
            async_dispatcher_send(self.hass, SIGNAL_NEW_PLAYER.format(self.entry.entry_id), player["id"])
        else:
            async_dispatcher_send(self.hass, SIGNAL_PLAYER_UPDATED.format(player["id"]))


async def async_setup_entry(hass: HomeAssistant, entry: AudioCentralConfigEntry) -> bool:
    client = AudioCentralClient(async_get_clientsession(hass), entry.data[CONF_URL], entry.data[CONF_TOKEN])
    hub = AudioCentralHub(hass, entry, client)
    try:
        await hub.async_refresh()
    except AudioCentralAuthError as err:
        raise ConfigEntryAuthFailed(str(err)) from err
    except AudioCentralError as err:
        raise ConfigEntryNotReady(str(err)) from err

    entry.runtime_data = hub
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    hub.events.start()
    return True


async def async_unload_entry(hass: HomeAssistant, entry: AudioCentralConfigEntry) -> bool:
    await entry.runtime_data.events.stop()
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
