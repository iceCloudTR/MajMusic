"use strict";

const viewer = document.querySelector("#viewer");
const stage = document.querySelector("#stage");
const status = document.querySelector("#status");
const query = new URL(location.href).searchParams;
document.documentElement.dataset.theme = query.get("theme") === "light" ? "light" : "dark";
let instance;
let stopped = false;
let heartbeat;

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
    clearInterval(heartbeat);
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
    status.textContent = "";
    heartbeat = setInterval(async () => {
        try {
            const alive = await fetch("alive", { cache: "no-store" });
            if (alive.ok) return;
        } catch { }
        void closePreview();
    }, 500);
}

start().catch(error => {
    if (!stopped) status.textContent = "MajView 加载失败";
    console.error(error);
});
