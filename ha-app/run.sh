#!/bin/sh
# Entrypoint of the Home Assistant app: map /data/options.json to Augora configuration.
set -eu

OPTIONS=/data/options.json

# Export NAME=<option> only when the option is set.
opt() {
    value=$(jq -r --arg key "$2" '.[$key] // empty' "$OPTIONS" 2>/dev/null || true)
    if [ -n "$value" ]; then
        export "$1=$value"
    fi
}

opt Providers__Spotify__ClientId spotify_client_id
opt Providers__TuneIn__Username tunein_username
opt Augora__StreamBaseUrl stream_base_url
opt Augora__PublicBaseUrl public_base_url

# A boolean, so "false" must come through (the opt helper skips it).
if [ "$(jq -r '.advertise_mdns' "$OPTIONS" 2>/dev/null || true)" = "false" ]; then
    export Augora__AdvertiseMdns=false
fi

# Home Assistant media and share folders (mounted read-only when the app is started).
export Providers__LocalFiles__Folders__0__Name=Media
export Providers__LocalFiles__Folders__0__Path=/media
export Providers__LocalFiles__Folders__1__Name=Share
export Providers__LocalFiles__Folders__1__Path=/share

# /data is owned by root in the app; Augora runs as the unprivileged app user.
chown -R app:app /data

cd /app
exec setpriv --reuid=app --regid=app --init-groups dotnet Augora.Server.dll
