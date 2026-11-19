"use strict";

const viewer = document.querySelector("#viewer");
const stage = document.querySelector("#stage");
const status = document.querySelector("#status");
const query = new URL(location.href).searchParams;
document.documentElement.dataset.theme = query.get("theme") === "light" ? "light" : "dark";
let instance;
let stopped = false;
let watchdog;
let clock = null;
let noteTimes = [];
let waveformStarted = false;
const generation = query.get("g");
const controller = new AbortController();
const wire = (name, args = {}) => name + "?" + new URLSearchParams({ g: generation, ...args });
const send = (method, value) => instance.SendMessage("HandleJSMessages", method, value);
const post = (name, data, args) => fetch(wire(name, args), { method:"POST", body:JSON.stringify(data), signal:controller.signal });
window.majPosition = (time, duration, playing) => { clock = { time, duration, playing }; };
window.majNoteTimes = (kind, times) => { noteTimes[kind] = times; };

// Decode only on the first full-screen request for this track. A low sample rate bounds PCM memory.
async function analyzeWaveform() {
    waveformStarted = true;
    try {
        const source = await fetch("track", {signal:controller.signal});
        if (!source.ok) return;
        const context = new OfflineAudioContext(1, 1, 8000);
        const audio = await context.decodeAudioData(await source.arrayBuffer());
        if (stopped) return;
        const peaks = Array(128).fill(0);
        for (let c = 0; c < audio.numberOfChannels; c++) {
            const samples = audio.getChannelData(c);
            for (let b = 0; b < peaks.length; b++) {
                let peak = 0;
                const end = Math.floor((b+1) * samples.length / peaks.length);
                for (let i = Math.floor(b * samples.length / peaks.length); i < end; i++) peak = Math.max(peak, Math.abs(samples[i]));
                peaks[b] = Math.max(peaks[b], Math.min(255, Math.round(peak * 255)));
            }
        }
        await post("waveform", peaks);
    } catch { /* The note density and transport remain available when the codec cannot decode a waveform. */ }
}

async function runTransport() {
    let revision = -1, seekRevision = -1, settingsKey = "";
    while (!stopped) {
        if (noteTimes) {
            await post("notes", noteTimes, { len: clock.duration });
            noteTimes = null;
        }
        const response = await fetch(wire("transport", { ack:revision, time:clock.time, len:clock.duration, play:clock.playing ? 1 : 0 }),
            {cache:"no-store", signal:controller.signal});
        if (response.status === 204) continue;
        if (!response.ok) { await closePreview(); return; }
        const intent = await response.json();
        if (stopped) return;
        if (intent.lightTheme != null) document.documentElement.dataset.theme = intent.lightTheme ? "light" : "dark";
        const options = JSON.stringify({...intent.options, gain:intent.gain});
        if (options !== settingsKey) { send("ConfigurePreview", options); settingsKey = options; }
        if (!intent.playing && clock.playing) send("SetPreviewPlaying", 0);
        if (seekRevision !== intent.seekRevision) {
            send("SeekPreview", Math.min(intent.position, clock.duration));
            seekRevision = intent.seekRevision;
        }
        if (intent.playing !== clock.playing) send("SetPreviewPlaying", intent.playing ? 1 : 0);
        if (intent.needWaveform && !waveformStarted) void analyzeWaveform();
        revision = intent.revision;
    }
}

function loadLevel(song, level) {
    const resource = name => new URL(name, document.baseURI).href;
    const request = [
        resource("chart"),
        resource("track"),
        resource("cover"),
        song.hasVideo ? resource("video.mp4") : "",
        "lv" + (level - 1)
    ].join("\n");
    instance.SendMessage("HandleJSMessages", "ReceiveMessage", request);
}

async function closePreview() {
    if (stopped) return;
    stopped = true;
    controller.abort();
    clearTimeout(watchdog);
    stage.hidden = true;
    status.textContent = "预览已关闭";
    if (instance) {
        try { await instance.Quit(); } catch { }
    }
}

window.addEventListener("pagehide", () => void closePreview());

async function start() {
    const response = await fetch("song.json");
    if (!response.ok) throw new Error("谱面已关闭");
    const song = await response.json();
    stage.classList.toggle("embedded", song.embeddedViewport === true);
    const requested = Number(query.get("level"));
    const level = song.difficulties.some(item => item.number === requested)
        ? requested : song.difficulties.at(-1).number;
    let engineReady;
    const ready = new Promise(resolve => { window.onUnityLoaded = engineReady = resolve; });
    instance = await createUnityInstance(viewer, {
        dataUrl: "runtime/Build.data",
        frameworkUrl: "runtime/Build.framework.js",
        codeUrl: "runtime/Build.wasm",
        streamingAssetsUrl: "runtime/StreamingAssets",
        companyName: "TeamMajdata",
        productName: "MajdataView",
        productVersion: "1",
        webglContextAttributes: { alpha: true, premultipliedAlpha: true, preserveDrawingBuffer: false, powerPreference: 2 }
    }, progress => {
        if (!stopped) status.textContent = "正在加载 MajView · " + Math.floor(progress * 100) + "%";
    });
    await ready;
    if (!song.embeddedViewport) {
        // The upstream renderer signals ready in Awake, before playback components initialize.
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    if (window.onUnityLoaded === engineReady) delete window.onUnityLoaded;
    if (stopped) {
        await instance.Quit();
        return;
    }
    loadLevel(song, level);
    stage.classList.toggle("reverse", query.get("direction") === "-1");
    stage.classList.toggle("no-motion", query.get("motion") === "off");
    stage.classList.add("entered");
    status.textContent = "正在准备谱面";
    const deadline = Date.now() + 60000;
    while (!clock && !stopped && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 50));
    if (stopped) return;
    if (!clock) throw new Error("谱面未就绪");
    status.textContent = "";
    clearTimeout(watchdog);
    await runTransport();
}

watchdog = setTimeout(() => { if (!stopped) void post("failed", null).catch(() => {}); }, 90000);
start().catch(error => {
    if (stopped) return;
    clearTimeout(watchdog);
    status.textContent = "MajView 加载失败";
    void post("failed", null).catch(() => {});
    console.error(error);
});
