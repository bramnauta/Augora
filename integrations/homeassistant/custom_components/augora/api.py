"""Client for the Augora REST API and its SignalR event hub."""

from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable
import json
import logging
from typing import Any
from urllib.parse import quote

import aiohttp

from .const import HUB_PATH

_LOGGER = logging.getLogger(__name__)

# SignalR JSON protocol: every message ends with the record separator.
RECORD_SEPARATOR = "\x1e"
MESSAGE_INVOCATION = 1
MESSAGE_PING = 6
MESSAGE_CLOSE = 7
PING_INTERVAL = 15
RECONNECT_DELAY = 10


class AugoraError(Exception):
    """An error reported by Augora (Problem Details with a code)."""

    def __init__(self, message: str, code: str | None = None, status: int | None = None, details: dict[str, Any] | None = None) -> None:
        super().__init__(message)
        self.code = code
        self.status = status
        self.details = details or {}


class AugoraAuthError(AugoraError):
    """The API token is invalid or was revoked."""


class AugoraClient:
    """REST calls with a long-lived API token (created under Account in the Augora UI)."""

    def __init__(self, session: aiohttp.ClientSession, base_url: str, token: str) -> None:
        self._session = session
        self.base_url = base_url.rstrip("/")
        self._token = token

    @property
    def headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._token}"}

    def absolute_url(self, url: str | None) -> str | None:
        """Server artwork is relative to the server."""
        if url and url.startswith("/"):
            return f"{self.base_url}{url}"
        return url

    async def get_players(self) -> list[dict[str, Any]]:
        return await self._request("GET", "/api/players")

    async def get_queue(self, player_id: str) -> dict[str, Any]:
        return await self._request("GET", f"/api/players/{quote(player_id, safe='')}/queue")

    async def browse(self, uri: str | None) -> dict[str, Any]:
        path = "/api/browse" if not uri else f"/api/browse?uri={quote(uri, safe='')}"
        return await self._request("GET", path)

    async def play(self, player_id: str, uri: str, option: str = "Replace") -> dict[str, Any]:
        return await self._request("POST", f"/api/players/{quote(player_id, safe='')}/play", {"uri": uri, "option": option})

    async def command(self, player_id: str, command: str) -> None:
        """pause, resume, stop, next or previous."""
        await self._request("POST", f"/api/players/{quote(player_id, safe='')}/{command}")

    async def set_volume(self, player_id: str, level: float) -> None:
        await self._request("PUT", f"/api/players/{quote(player_id, safe='')}/volume", {"level": level})

    async def set_muted(self, player_id: str, muted: bool) -> None:
        await self._request("PUT", f"/api/players/{quote(player_id, safe='')}/mute", {"muted": muted})

    async def seek(self, player_id: str, position: float) -> None:
        await self._request("POST", f"/api/players/{quote(player_id, safe='')}/seek", {"positionSeconds": position})

    async def set_shuffle(self, player_id: str, enabled: bool) -> None:
        await self._request("PUT", f"/api/players/{quote(player_id, safe='')}/queue/shuffle?enabled={str(enabled).lower()}")

    async def set_repeat(self, player_id: str, mode: str) -> None:
        """Off, One or All."""
        await self._request("PUT", f"/api/players/{quote(player_id, safe='')}/queue/repeat?mode={mode}")

    async def _request(self, method: str, path: str, body: Any = None) -> Any:
        try:
            async with self._session.request(
                method, f"{self.base_url}{path}", json=body, headers=self.headers, timeout=aiohttp.ClientTimeout(total=30)
            ) as response:
                if response.status == 401:
                    raise AugoraAuthError("The API token was rejected", status=401)
                if response.status >= 400:
                    raise await _problem(response)
                if response.status == 204 or response.content_length == 0:
                    return None
                return await response.json()
        except aiohttp.ClientError as err:
            raise AugoraError(f"Cannot reach Augora: {err}") from err


async def _problem(response: aiohttp.ClientResponse) -> AugoraError:
    try:
        problem = await response.json(content_type=None)
    except (aiohttp.ContentTypeError, json.JSONDecodeError, ValueError):
        problem = {}
    if not isinstance(problem, dict):
        problem = {}
    return AugoraError(problem.get("title") or response.reason or str(response.status), problem.get("code"), response.status, problem)


class AugoraEvents:
    """Receives PlayerUpdated, PlayerRemoved and QueueUpdated from the hub and reconnects when it drops."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        client: AugoraClient,
        handler: Callable[[str, list[Any]], Awaitable[None] | None],
        on_connected: Callable[[], Awaitable[None]] | None = None,
    ) -> None:
        self._session = session
        self._client = client
        self._handler = handler
        self._on_connected = on_connected
        self._task: asyncio.Task[None] | None = None
        self.connected = False

    def start(self) -> None:
        self._task = asyncio.create_task(self._run(), name="augora-events")

    async def stop(self) -> None:
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass

    async def _run(self) -> None:
        url = f"{self._client.base_url}{HUB_PATH}".replace("http://", "ws://", 1).replace("https://", "wss://", 1)
        while True:
            try:
                # WebSockets only, so no negotiate request is needed.
                async with self._session.ws_connect(url, headers=self._client.headers, heartbeat=None) as ws:
                    await ws.send_str(json.dumps({"protocol": "json", "version": 1}) + RECORD_SEPARATOR)
                    pinger = asyncio.create_task(self._ping(ws))
                    try:
                        await self._receive(ws)
                    finally:
                        pinger.cancel()
            except asyncio.CancelledError:
                raise
            except (aiohttp.ClientError, asyncio.TimeoutError) as err:
                _LOGGER.debug("Augora event hub unavailable: %s", err)
            finally:
                if self.connected:
                    self.connected = False
                    await self._dispatch("Disconnected", [])
            await asyncio.sleep(RECONNECT_DELAY)

    async def _receive(self, ws: aiohttp.ClientWebSocketResponse) -> None:
        handshake_done = False
        buffer = ""
        async for message in ws:
            if message.type != aiohttp.WSMsgType.TEXT:
                if message.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                    return
                continue
            buffer += message.data
            *frames, buffer = buffer.split(RECORD_SEPARATOR)
            for frame in frames:
                if not frame:
                    continue
                data = json.loads(frame)
                if not handshake_done:
                    if data.get("error"):
                        raise aiohttp.ClientError(data["error"])
                    handshake_done = True
                    self.connected = True
                    if self._on_connected:
                        await self._on_connected()
                    continue
                kind = data.get("type")
                if kind == MESSAGE_INVOCATION:
                    await self._dispatch(data.get("target", ""), data.get("arguments", []))
                elif kind == MESSAGE_CLOSE:
                    return

    async def _dispatch(self, target: str, arguments: list[Any]) -> None:
        try:
            result = self._handler(target, arguments)
            if asyncio.iscoroutine(result):
                await result
        except Exception:  # noqa: BLE001 - one bad event must not end the connection
            _LOGGER.exception("Handling Augora event %s failed", target)

    @staticmethod
    async def _ping(ws: aiohttp.ClientWebSocketResponse) -> None:
        while True:
            await asyncio.sleep(PING_INTERVAL)
            await ws.send_str(json.dumps({"type": MESSAGE_PING}) + RECORD_SEPARATOR)
