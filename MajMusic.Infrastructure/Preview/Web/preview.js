"use strict";

const viewer = document.querySelector("#viewer");
const difficulty = document.querySelector("#difficulty");
const status = document.querySelector("#status");
const settingsForm = document.querySelector("#settings");
const applySettings = document.querySelector("#apply-settings");
const settingFields = document.querySelector("#setting-fields");
const settingsKey = "MajdataSettings";
const defaults = [0, 70, 70, 6, -3, 6, -10, -10, -10, -10, -10, -15, -20, 1];
const settingDefinitions = [
    [1, "note-speed", "音符流速"], [2, "touch-speed", "触摸流速"],
    [4, "bgm", "音乐 dB"], [5, "answer", "节拍 dB"], [6, "judge", "打击 dB"],
    [7, "slide", "滑条 dB"], [8, "break", "绝赞 dB"], [9, "ex", "EX dB"],
    [10, "touch", "触摸 dB"], [11, "hanabi", "烟花 dB"], [12, "others", "其他 dB"]
];
let instance;
let song;
let stopped = false;
let heartbeat;
let changingLevel = false;

function readSettings() {
    try {
        const values = localStorage.getItem(settingsKey)?.split(",").map(Number);
        if (values?.length === defaults.length && values.every(Number.isFinite)) return values;
    } catch { }
    return defaults.slice();
}

function fillSettings() {
    settingFields.replaceChildren();
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

function reloadLevel() {
    changingLevel = true;
    const url = new URL(location.href);
    url.searchParams.set("level", difficulty.value);
    location.replace(url);
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
    if (serialized === previous) return;
    try {
        localStorage.setItem(settingsKey, serialized);
    } catch {
        status.textContent = "浏览器未允许保存预览设置";
        return;
    }
    reloadLevel();
});

function loadLevel() {
    const resource = name => new URL(name, document.baseURI).href;
    const level = Number(difficulty.value) - 1;
    const request = [
        resource("chart"),
        resource("track"),
        resource("cover"),
        song.hasVideo ? resource("video.mp4") : "",
        "lv" + level
    ].join("\n");
    instance.SendMessage("HandleJSMessages", "ReceiveMessage", request);
}

async function closePreview() {
    if (stopped) return;
    stopped = true;
    clearInterval(heartbeat);
    difficulty.disabled = true;
    applySettings.disabled = true;
    viewer.hidden = true;
    status.textContent = "预览已关闭";
    if (instance) {
        try { await instance.Quit(); } catch { }
    }
}

difficulty.addEventListener("change", () => {
    if (!instance || stopped) return;
    reloadLevel();
});
window.addEventListener("pagehide", () => {
    if (!changingLevel) navigator.sendBeacon("closed");
    void closePreview();
});
document.querySelector("#options").addEventListener("toggle", event => {
    if (event.currentTarget.open) fillSettings();
});

async function start() {
    const response = await fetch("song.json");
    if (!response.ok) throw new Error("谱面已关闭");
    song = await response.json();
    const names = ["Easy", "Basic", "Advanced", "Expert", "Master", "Re:Master", "UTAGE"];
    for (const item of song.difficulties) {
        const option = document.createElement("option");
        option.value = item.number;
        option.textContent = [names[item.number - 1], item.level].filter(Boolean).join(" ");
        difficulty.append(option);
    }
    difficulty.selectedIndex = difficulty.options.length - 1;
    const requestedLevel = new URL(location.href).searchParams.get("level");
    if (song.difficulties.some(item => String(item.number) === requestedLevel)) {
        difficulty.value = requestedLevel;
    }
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
    loadLevel();
    difficulty.disabled = false;
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

fillSettings();
start().catch(error => {
    if (!stopped) status.textContent = "MajView 加载失败";
    console.error(error);
});
