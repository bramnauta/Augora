// Plays AudioCentral streams in this browser tab and reports the state back to .NET.
// The server drives it through BrowserPlayerService; volume and mute only affect this tab.

// Shortest valid WAV: playing it during a click "unlocks" audio for later, programmatic playback.
const SILENCE = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
const POSITION_REPORT_MS = 10000;

let audio;
let dotnet;
let current = null;
let lastPositionReport = 0;

function state() {
    if (!current || !audio.currentSrc) return "Idle";
    if (audio.ended) return "Idle";
    if (audio.paused) return "Paused";
    return audio.readyState < 3 ? "Buffering" : "Playing";
}

function report(event = "None", error = null) {
    if (!dotnet) return;
    const position = current && Number.isFinite(audio.currentTime) ? audio.currentTime : null;
    dotnet.invokeMethodAsync("OnStateChanged", {
        state: event === "None" ? state() : "Idle",
        volume: audio.volume,
        muted: audio.muted,
        positionSeconds: position,
        event,
        error,
    });
}

export function init(dotnetRef, volume) {
    dotnet = dotnetRef;
    if (audio) return;

    audio = new Audio();
    audio.preload = "none";
    audio.volume = volume ?? 1;

    for (const name of ["playing", "pause", "waiting", "volumechange", "loadstart"]) {
        audio.addEventListener(name, () => report());
    }
    audio.addEventListener("timeupdate", () => {
        const now = Date.now();
        if (now - lastPositionReport > POSITION_REPORT_MS) {
            lastPositionReport = now;
            report();
        }
    });
    audio.addEventListener("ended", () => {
        if (!current) return;
        current = null;
        report("Finished");
    });
    audio.addEventListener("error", () => {
        if (!current) return;
        current = null;
        report("Error", audio.error?.message || "Playback error");
    });

    if ("mediaSession" in navigator) {
        navigator.mediaSession.setActionHandler("play", () => audio.play());
        navigator.mediaSession.setActionHandler("pause", () => audio.pause());
    }
}

export async function unlock() {
    if (current) return true;
    try {
        audio.src = SILENCE;
        await audio.play();
        return true;
    } catch {
        return false;
    } finally {
        if (!current) {
            audio.removeAttribute("src");
            audio.load();
        }
    }
}

export async function play(media) {
    current = media;
    audio.src = media.streamUrl;
    if ("mediaSession" in navigator && "MediaMetadata" in window) {
        navigator.mediaSession.metadata = new MediaMetadata({
            title: media.title,
            artist: media.subtitle ?? "",
            artwork: media.imageUrl ? [{ src: media.imageUrl }] : [],
        });
    }
    return await start();
}

/** Returns "ok", "blocked" (autoplay policy, needs a click) or an error message. */
export async function start() {
    try {
        await audio.play();
        return "ok";
    } catch (e) {
        if (e.name === "NotAllowedError") {
            report();
            return "blocked";
        }
        if (e.name === "AbortError") return "ok";
        current = null;
        report("Error", e.message);
        return e.message;
    }
}

/** Sends the current state, e.g. right after (re)registering. */
export function reportState() {
    report();
}

export function pause() {
    audio.pause();
}

export function stop() {
    current = null;
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    if ("mediaSession" in navigator) navigator.mediaSession.metadata = null;
    report();
}

export function setVolume(level) {
    audio.volume = Math.min(1, Math.max(0, level));
}

export function setMuted(muted) {
    audio.muted = muted;
}

export function describe() {
    const ua = navigator.userAgent;
    const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : "Browser";
    const os = /Android/.test(ua) ? "Android" : /iPhone|iPad/.test(ua) ? "iOS" : /Windows/.test(ua) ? "Windows" : /Mac OS/.test(ua) ? "macOS" : /Linux/.test(ua) ? "Linux" : "";
    return { browser, os, mobile: /Mobi|Android|iPhone/.test(ua) };
}
