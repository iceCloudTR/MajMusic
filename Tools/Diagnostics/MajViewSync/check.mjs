import assert from "node:assert/strict";
import { createReadStream, statSync } from "node:fs";
import { createServer } from "node:http";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

// Invoked from the repository root. The renderer and media are real; only the native transport is driven here.
const [fixture, build = ".work/majview-runtime-custom"] = process.argv.slice(2);
assert.ok(fixture, "Pass the generated 20-second sync fixture directory");
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(resolve(process.env.PLAYWRIGHT_MODULE)).href : "playwright-core");
const files = new Map();
function file(route, directory, name, type) {
    const path = resolve(directory, name);
    files.set(route, { path, length: statSync(path).size, type });
}
file("/", "MajMusic.Infrastructure/Preview/Web", "index.html", "text/html");
file("/preview.js", "MajMusic.Infrastructure/Preview/Web", "preview.js", "application/javascript");
file("/track", fixture, "track.mp3", "audio/mpeg");
file("/cover", fixture, "bg.png", "image/png");
file("/chart", fixture, "maidata.txt", "text/plain");
file("/video.mp4", fixture, "pv.mp4", "video/mp4");
for (const [name, type] of [["Build.loader.js", "application/javascript"], ["Build.framework.js", "application/javascript"],
    ["Build.wasm", "application/wasm"], ["Build.data", "application/octet-stream"]]) {
    file("/runtime/" + name, build, name, type);
}

let intent, report;
const server = createServer((req, res) => {
    const { pathname, searchParams } = new URL(req.url, "http://localhost");
    if (pathname === "/transport") {
        report = Object.fromEntries(searchParams);
        if (+report.ack !== intent.revision) return res.end(JSON.stringify(intent));
        return setTimeout(() => res.writeHead(204).end(), 80);
    }
    if (pathname === "/song.json") return res.end(JSON.stringify({ hasVideo: true, embeddedViewport: true, difficulties: [{ number: 7 }] }));
    if (req.method === "POST") { req.resume(); return res.writeHead(204).end(); }
    const asset = files.get(pathname);
    if (!asset) return res.writeHead(404).end();
    // Match the real loopback host: without byte ranges Chromium may return to the first PV frame on every seek.
    const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
    const start = Number(range?.[1] || 0), end = range?.[2] ? Number(range[2]) : asset.length - 1;
    if (start > end || end >= asset.length) return res.writeHead(416).end();
    res.setHeader("Accept-Ranges", "bytes");
    res.setHeader("Content-Length", end - start + 1);
    res.setHeader("Content-Type", asset.type);
    if (range) { res.statusCode = 206; res.setHeader("Content-Range", `bytes ${start}-${end}/${asset.length}`); }
    createReadStream(asset.path, { start, end }).pipe(res);
});

// Observe the actually scheduled Web Audio buffer and HTML video in the same turn as the chart clock report.
function observeOutput() {
    let video, track, offset, started, forward;
    const samples = [], hits = [];
    const create = document.createElement.bind(document);
    document.createElement = function (name, ...args) {
        const element = create(name, ...args);
        if (name === "video") video = element;
        return element;
    };
    const audioTime = () => track && offset + (track.context.currentTime - started) * track.playbackRate.value;
    const start = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (when = 0, seek = 0, ...args) {
        if (Math.abs(this.buffer?.duration - 20) < 0.2) {
            track = this;
            offset = seek;
            started = Math.max(this.context.currentTime, when);
        } else if (track) {
            hits.push(audioTime());
        }
        return start.call(this, when, seek, ...args);
    };
    Object.defineProperty(window, "majPosition", {
        get: () => forward,
        set: callback => { forward = (t, length, playing) => {
            samples.push({ t, playing, audio: audioTime(), video: video?.currentTime, muted: video?.muted || video?.volume === 0 });
            callback(t, length, playing);
        }; }
    });
    window.readOutput = () => ({ samples: samples.splice(0), hits: hits.splice(0), video: video?.currentTime, paused: video?.paused });
}

async function until(predicate, timeout = 10000) {
    const deadline = Date.now() + timeout;
    while (!predicate() && Date.now() < deadline) await delay(20);
    assert.ok(predicate(), `Timed out. Last report: ${JSON.stringify(report)}`);
}
async function change(patch) {
    intent = { ...intent, ...patch, revision: intent.revision + 1 };
    await until(() => +report?.ack === intent.revision);
}
const seek = (position, patch) => change({ position, seekRevision: intent.seekRevision + 1, ...patch });

async function verify(browser, mobile) {
    const page = await browser.newPage({ viewport: mobile ? { width: 390, height: 844 } : { width: 930, height: 930 }, isMobile: mobile, hasTouch: mobile });
    const read = () => page.evaluate(() => window.readOutput());
    report = null;
    intent = { generation: 1, revision: 1, seekRevision: 1, position: 0, playing: false, gain: 1,
        options: { noteSpeed: 7, touchSpeed: 7, offsetMs: 0, shade: 0.6, effects: 0.55, speed: 1 } };
    await page.addInitScript(observeOutput);
    await page.goto(`http://127.0.0.1:${server.address().port}/?g=1&level=7`);
    await until(() => +report?.ack === 1, 65000);
    for (const speed of [1, 0.25, 0.5, 1.5, 2]) {
        await seek(0, { playing: false, options: { ...intent.options, speed } });
        await read();
        await change({ playing: true });
        await until(() => +report.time > speed * 2.2);
        const { samples, hits } = await read();
        const stable = samples.filter(s => s.playing && s.t > speed * 0.45);
        assert.ok(stable.length >= 5 && stable.every(s => s.muted), "Expected live chart reports and muted PV audio");
        const audioError = Math.max(...stable.map(s => Math.abs(s.t - s.audio)));
        const pvError = Math.max(...stable.map(s => Math.abs(s.t - s.video)));
        assert.ok(audioError < .06 && pvError < .18, `At ${speed}×: chart/audio ${audioError}s, chart/PV ${pvError}s`);
        if (speed === 1) for (const beat of [.25, .75, 1.25, 1.75]) {
            assert.ok(hits.some(t => t >= beat - .035 && t < beat + .09), `Missing beat at ${beat}s`);
        }
        await seek(7.3, { playing: true });
        await read();
        await delay(650);
        const moving = (await read()).samples.filter(s => s.playing && s.t >= 7.3);
        assert.ok(moving.length >= 2 && moving.every(s => Math.abs(s.audio - s.t) < .065), `Wrong buffer offset after a ${speed}× seek`);
        console.log(`${mobile ? "phone" : "desktop"} ${speed}× max chart/audio ${(audioError*1000).toFixed(1)}ms, chart/PV ${(pvError*1000).toFixed(1)}ms`);
    }
    // Pause while a playing seek is in flight, then move both backwards and forwards while parked.
    await seek(10, { playing: true });
    await change({ playing: false });
    const parked = +report.time;
    if (mobile) await page.setViewportSize({ width: 844, height: 390 });
    await delay(1200);
    let output = await read();
    assert.ok(output.paused && Math.abs(output.video - parked) < .1 && Math.abs(+report.time - parked) < .005, "PV resumed after pause");
    for (const t of [2.75, 12.5]) {
        await seek(t, { playing: false });
        await delay(250);
        output = await read();
        assert.ok(output.paused && Math.abs(output.video - t) < .075 && Math.abs(+report.time - t) < .01, "Paused seek missed its target");
    }
    await seek(19.3, { playing: true });
    await until(() => report.play === "0" && +report.time >= 19.99);
    await page.close();
}

await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
let browser;
try {
    browser = await chromium.launch({ executablePath: process.env.CHROMIUM, headless: true,
        args: ["--enable-unsafe-swiftshader", "--autoplay-policy=no-user-gesture-required"] });
    await verify(browser, false);
    await verify(browser, true);
} finally {
    await browser?.close();
    server.closeAllConnections();
    server.close();
}
