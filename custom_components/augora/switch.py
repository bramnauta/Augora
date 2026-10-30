"""Autoplay (track radio) of an Augora player as a switch."""

from __future__ import annotations

from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.dispatcher import async_dispatcher_connect
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import AugoraConfigEntry, AugoraHub
from .api import AugoraError
from .const import DOMAIN, SIGNAL_NEW_PLAYER, SIGNAL_PLAYER_UPDATED, SIGNAL_QUEUE_UPDATED


async def async_setup_entry(hass: HomeAssistant, entry: AugoraConfigEntry, async_add_entities: AddConfigEntryEntitiesCallback) -> None:
    hub = entry.runtime_data
    async_add_entities(AugoraAutoplaySwitch(hub, player_id) for player_id in hub.players)

    @callback
    def add_player(player_id: str) -> None:
        async_add_entities([AugoraAutoplaySwitch(hub, player_id)])

    entry.async_on_unload(async_dispatcher_connect(hass, SIGNAL_NEW_PLAYER.format(entry.entry_id), add_player))


class AugoraAutoplaySwitch(SwitchEntity):
    """When the queue ends, the player keeps going with tracks similar to the last one."""

    _attr_has_entity_name = True
    _attr_should_poll = False
    _attr_translation_key = "autoplay"

    def __init__(self, hub: AugoraHub, player_id: str) -> None:
        self._hub = hub
        self._player_id = player_id
        self._attr_unique_id = f"{hub.entry.entry_id}_{player_id}_autoplay"
        # The same device as the media player of this player.
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, f"{hub.entry.entry_id}_{player_id}")})

    async def async_added_to_hass(self) -> None:
        self.async_on_remove(async_dispatcher_connect(self.hass, SIGNAL_PLAYER_UPDATED.format(self._player_id), self._updated))
        self.async_on_remove(async_dispatcher_connect(self.hass, SIGNAL_QUEUE_UPDATED.format(self._player_id), self._updated))
        if self._player_id not in self._hub.queues:
            try:
                self._hub.queues[self._player_id] = await self._hub.client.get_queue(self._player_id)
            except AugoraError:
                pass

    @callback
    def _updated(self) -> None:
        self.async_write_ha_state()

    @property
    def available(self) -> bool:
        return self._hub.connected and bool((self._hub.players.get(self._player_id) or {}).get("available"))

    @property
    def is_on(self) -> bool | None:
        queue = self._hub.queues.get(self._player_id)
        return None if queue is None else bool(queue.get("autoplay"))

    async def async_turn_on(self, **kwargs: Any) -> None:
        await self._set(True)

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self._set(False)

    async def _set(self, enabled: bool) -> None:
        try:
            await self._hub.client.set_autoplay(self._player_id, enabled)
        except AugoraError as err:
            raise HomeAssistantError(str(err)) from err
