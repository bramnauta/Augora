"""Config flow: the Augora url and an API token created under Account in its UI."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any
from urllib.parse import urlsplit

import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .api import AugoraAuthError, AugoraClient, AugoraError
from .const import CONF_TOKEN, CONF_URL, DOMAIN


class AugoraConfigFlow(ConfigFlow, domain=DOMAIN):
    """Set up a connection to an Augora server."""

    VERSION = 1

    _discovered_url: str = ""

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            url = user_input[CONF_URL].strip().rstrip("/")
            errors = await self._validate(url, user_input[CONF_TOKEN])
            if not errors:
                await self.async_set_unique_id(url.lower())
                self._abort_if_unique_id_configured()
                return self.async_create_entry(title=url.split("://", 1)[-1], data={CONF_URL: url, CONF_TOKEN: user_input[CONF_TOKEN]})

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {
                    vol.Required(CONF_URL, default=(user_input or {}).get(CONF_URL, "http://")): str,
                    vol.Required(CONF_TOKEN): str,
                }
            ),
            errors=errors,
        )

    async def async_step_zeroconf(self, discovery_info: ZeroconfServiceInfo) -> ConfigFlowResult:
        """A server announced itself on the network; only the API token is still needed."""
        host = discovery_info.host
        url = f"http://{f'[{host}]' if ':' in host else host}:{discovery_info.port}"

        # The same server may be configured under its name or another address.
        for entry in self._async_current_entries():
            configured = urlsplit(entry.data[CONF_URL])
            if configured.hostname == host and (configured.port or 80) == discovery_info.port:
                return self.async_abort(reason="already_configured")

        await self.async_set_unique_id(url.lower())
        self._abort_if_unique_id_configured()
        self._discovered_url = url
        self.context["title_placeholders"] = {"name": discovery_info.name.split(".", 1)[0]}
        return await self.async_step_discovery_confirm()

    async def async_step_discovery_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            errors = await self._validate(self._discovered_url, user_input[CONF_TOKEN])
            if not errors:
                return self.async_create_entry(
                    title=self._discovered_url.split("://", 1)[-1],
                    data={CONF_URL: self._discovered_url, CONF_TOKEN: user_input[CONF_TOKEN]},
                )

        return self.async_show_form(
            step_id="discovery_confirm",
            data_schema=vol.Schema({vol.Required(CONF_TOKEN): str}),
            description_placeholders={"url": self._discovered_url},
            errors=errors,
        )

    async def async_step_reauth(self, entry_data: Mapping[str, Any]) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            errors = await self._validate(entry.data[CONF_URL], user_input[CONF_TOKEN])
            if not errors:
                return self.async_update_reload_and_abort(entry, data_updates={CONF_TOKEN: user_input[CONF_TOKEN]})

        return self.async_show_form(step_id="reauth_confirm", data_schema=vol.Schema({vol.Required(CONF_TOKEN): str}), errors=errors)

    async def _validate(self, url: str, token: str) -> dict[str, str]:
        client = AugoraClient(async_get_clientsession(self.hass), url, token)
        try:
            await client.get_players()
        except AugoraAuthError:
            return {"base": "invalid_auth"}
        except AugoraError:
            return {"base": "cannot_connect"}
        return {}
