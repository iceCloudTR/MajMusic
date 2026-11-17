"use strict";

const viewer = document.querySelector("#viewer");
const status = document.querySelector("#status");
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
    viewer.hidden = true;
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
    const query = new URL(location.href).searchParams;
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
        productVersion: "1"
    }, progress => {
        if (!stopped) status.textContent = "正在加载 MajView · " + Math.floor(progress * 100) + "%";
    });
    await Promise.race([ready, new Promise(resolve => setTimeout(resolve, 1500))]);
    await new Promise(resolve => setTimeout(resolve, 500));
    if (window.onUnityLoaded === engineReady) delete window.onUnityLoaded;
    if (stopped) {
        await instance.Quit();
        return;
    }
    loadLevel(song, level);
    viewer.classList.toggle("reverse", query.get("direction") === "-1");
    viewer.classList.toggle("no-motion", query.get("motion") === "off");
    viewer.classList.add("entered");
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
