"use strict";

const viewer = document.querySelector("#viewer");
const status = document.querySelector("#status");
const options = document.querySelector("#options");
const settingsForm = document.querySelector("#settings");
const applySettings = document.querySelector("#apply-settings");
const settingFields = document.querySelector("#setting-fields");
const videoOption = document.querySelector("#video-option");
const showVideo = document.querySelector("#show-video");
const settingsKey = "MajdataSettings";
const videoKey = "MajMusicShowPreviewVideo";
const defaults = [0, 70, 70, 6, -3, 6, -10, -10, -10, -10, -10, -15, -20, 1];
const settingDefinitions = [
    [1, "note-speed", "音符流速"], [2, "touch-speed", "触摸流速"],
    [4, "bgm", "音乐 dB"], [5, "answer", "节拍 dB"], [6, "judge", "打击 dB"],
    [7, "slide", "滑条 dB"], [8, "break", "绝赞 dB"], [9, "ex", "EX dB"],
    [10, "touch", "触摸 dB"], [11, "hanabi", "烟花 dB"], [12, "others", "其他 dB"]
];
let instance;
let stopped = false;
let heartbeat;

function readSettings() {
    try {
        const values = localStorage.getItem(settingsKey)?.split(",").map(Number);
        if (values?.length === defaults.length && values.every(Number.isFinite)) return values;
    } catch { }
    return defaults.slice();
}

function fillSettings() {
    settingFields.replaceChildren();
    showVideo.checked = readVideoPreference();
    const values = readSettings();
    for (const [index, name, text] of settingDefinitions) {
        const label = document.createElement("label");
        label.textContent = text;
        const input = document.createElement("input");
        const speed = index < 4;
        input.type = "number";
        input.name = name;
        input.required = true;
        input.min = speed ? "0.1" : "-80";
        input.max = "20";
        input.step = speed ? "0.1" : "1";
        input.value = String(speed ? values[index] / 10 : Math.round(values[index]));
        label.append(input);
        settingFields.append(label);
    }
}

function readVideoPreference() {
    try { return localStorage.getItem(videoKey) !== "false"; } catch { return true; }
}

settingsForm.addEventListener("submit", event => {
    event.preventDefault();
    if (!instance || stopped) return;
    const values = readSettings();
    const previous = values.join(",");
    for (const [index, name] of settingDefinitions) {
        const input = settingsForm.elements.namedItem(name);
        values[index] = index < 4 ? Math.round(input.valueAsNumber * 10) : input.valueAsNumber;
    }
    const serialized = values.join(",");
    const videoChanged = !videoOption.hidden && showVideo.checked !== readVideoPreference();
    if (serialized === previous && !videoChanged) return;
    try {
        localStorage.setItem(settingsKey, serialized);
        if (videoChanged) localStorage.setItem(videoKey, String(showVideo.checked));
    } catch {
        status.textContent = "无法保存预览设置";
        return;
    }
    location.replace(location.href);
});

function loadLevel(song, level) {
    const resource = name => new URL(name, document.baseURI).href;
    const request = [
        resource("chart"),
        resource("track"),
        resource("cover"),
        song.hasVideo && readVideoPreference() ? resource("video.mp4") : "",
        "lv" + (level - 1)
    ].join("\n");
    instance.SendMessage("HandleJSMessages", "ReceiveMessage", request);
}

async function closePreview() {
    if (stopped) return;
    stopped = true;
    clearInterval(heartbeat);
    options.hidden = true;
    applySettings.disabled = true;
    viewer.hidden = true;
    status.textContent = "预览已关闭";
    if (instance) {
        try { await instance.Quit(); } catch { }
    }
}

window.addEventListener("pagehide", () => void closePreview());
options.addEventListener("toggle", event => {
    if (event.currentTarget.open) fillSettings();
});
document.addEventListener("pointerdown", event => {
    if (options.open && !options.contains(event.target)) {
        options.open = false;
    }
});
document.addEventListener("keydown", event => {
    if (event.key === "Escape" && options.open) {
        event.preventDefault();
        options.open = false;
        options.querySelector("summary").focus();
    }
});

async function start() {
    const response = await fetch("song.json");
    if (!response.ok) throw new Error("谱面已关闭");
    const song = await response.json();
    videoOption.hidden = !song.hasVideo;
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
    options.hidden = false;
    applySettings.disabled = false;
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
