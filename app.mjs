#!/usr/bin/env node
/**
 * ANS 每日签到 · 本地控制台服务（多账号）
 *
 * 零依赖 HTTP 服务，只监听 127.0.0.1。提供：
 *   GET    /                    控制台界面
 *   GET    /api/ping            存活探测（桌面启动器用）
 *   GET    /api/status          所有账号的登录态、积分、今日状态、配置
 *   GET    /api/runs            执行历史（runs.jsonl）
 *   GET    /api/logs            今天的运行日志
 *   GET    /api/export          导出执行历史
 *   POST   /api/run?account=id  立即执行（NDJSON 流式返回日志；不传 account 则跑全部）
 *   POST   /api/login           打开浏览器添加/刷新账号
 *   POST   /api/config          保存任务开关
 *   PATCH  /api/accounts/:id    启用/停用账号
 *   DELETE /api/accounts/:id    删除账号
 */
import http from "node:http";
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import crypto from "node:crypto";
import { spawn } from "node:child_process";
import {
  ROOT,
  listAccounts,
  getAccount,
  patchAccount,
  deleteAccount,
  saveAccount,
  migrateLegacy,
} from "./lib/accounts.mjs";
import { loginWithPassword, validateCookie } from "./lib/ans-auth.mjs";

const ORIGIN = "https://ans.cauai.fun";
const UI_FILE = path.join(ROOT, "ui.html");
const CONFIG_FILE = path.join(ROOT, "config.json");
const LOG_DIR = path.join(ROOT, "logs");
const RUNS_FILE = path.join(LOG_DIR, "runs.jsonl");
const CHECKIN = path.join(ROOT, "checkin.mjs");
const GRAB = path.join(ROOT, "grab-cookie.mjs");
const UI_STATE_FILE = path.join(ROOT, "ui-state.json");

const HOST = "127.0.0.1";
const DEFAULT_PORT = Number(process.env.ANS_PORT) || 8787;

const QUEST_CATALOG = [
  { id: "daily_checkin", zh: "每日算力签到", reward: "+5 积分 +1 币" },
  { id: "daily_vibehub_read", zh: "VibeHub 沉浸充电 60 秒", reward: "+5 积分 +1 币" },
  { id: "daily_explore_star", zh: "星火助威 · 点亮 Star", reward: "+5 积分" },
  { id: "daily_agent_feed", zh: "小恐龙特训 · 投喂/对话", reward: "+3 积分" },
];

const DEFAULT_CONFIG = {
  quests: QUEST_CATALOG.map((q) => q.id),
  retries: 3,
  retryDelayMs: 2500,
  timeoutMs: 20000,
  webhook: "",
  port: DEFAULT_PORT,
  schedule: ["00:10", "09:00", "16:30"],
};

// ------------------------------------------------------------------ 工具

function readJson(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return fallback;
  }
}

function writeJson(file, value) {
  fs.writeFileSync(file, JSON.stringify(value, null, 2), "utf8");
}

function loadConfig() {
  const user = readJson(CONFIG_FILE, {}) || {};
  return { ...DEFAULT_CONFIG, ...user };
}

function shanghaiDate(d = new Date()) {
  try {
    return new Intl.DateTimeFormat("en-CA", {
      timeZone: "Asia/Shanghai",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(d);
  } catch {
    return d.toISOString().slice(0, 10);
  }
}

function shanghaiClock(d = new Date()) {
  try {
    return new Intl.DateTimeFormat("sv-SE", {
      timeZone: "Asia/Shanghai",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false,
    }).format(d);
  } catch {
    return d.toISOString().slice(11, 19);
  }
}

function readRuns(limit = 300) {
  if (!fs.existsSync(RUNS_FILE)) return [];
  const lines = fs.readFileSync(RUNS_FILE, "utf8").split(/\r?\n/).filter(Boolean);
  const out = [];
  for (let i = lines.length - 1; i >= 0 && out.length < limit; i--) {
    try {
      out.push(JSON.parse(lines[i]));
    } catch {
      /* 跳过损坏行 */
    }
  }
  return out.reverse();
}

function json(res, code, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(code, {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
  });
  res.end(body);
}

async function readBody(req, limitBytes = 64 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on("data", (c) => {
      size += c.length;
      if (size > limitBytes) {
        reject(new Error("payload too large"));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

// ------------------------------------------------------------------ 站点探测

const sessionCache = new Map(); // id -> { at, value }
const SESSION_TTL = 20000;

async function siteFetch(pathname, cookie) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), 15000);
  try {
    return await fetch(ORIGIN + pathname, {
      signal: ac.signal,
      headers: {
        accept: "application/json, text/plain, */*",
        "accept-language": "zh-CN,zh;q=0.9",
        cookie,
        origin: ORIGIN,
        referer: `${ORIGIN}/workspace/benefits`,
        "user-agent":
          "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
      },
    });
  } finally {
    clearTimeout(timer);
  }
}

function extractPoints(p) {
  if (p == null) return null;
  for (const v of [p.points, p.balance, p.balancePoints, p.data?.points, p.data?.balance]) {
    if (typeof v === "number" && Number.isFinite(v)) return Math.round(v * 100) / 100;
  }
  return null;
}

async function probeAccount(account, force = false) {
  const cached = sessionCache.get(account.id);
  if (!force && cached && Date.now() - cached.at < SESSION_TTL) return cached.value;

  let value;
  try {
    const sess = await siteFetch("/api/auth/session", account.cookie);
    const sessJson = await sess.json().catch(() => null);
    if (!sessJson || sess.status === 401) {
      value = { ok: false, reason: "登录态已失效", points: null };
    } else {
      const pts = await siteFetch("/api/me/points", account.cookie);
      const ptsJson = await pts.json().catch(() => null);
      value = {
        ok: pts.status === 200,
        reason: pts.status === 200 ? "" : `积分接口 HTTP ${pts.status}`,
        points: extractPoints(ptsJson),
      };
    }
  } catch (err) {
    value = { ok: false, reason: `网络错误：${err?.message || err}`, points: null };
  }

  sessionCache.set(account.id, { at: Date.now(), value });
  return value;
}

// ------------------------------------------------------------------ 状态汇总

function normalizeRunAccounts(run) {
  if (Array.isArray(run.accounts)) return run.accounts;
  // 旧版单账号记录
  if (run.account) {
    return [
      {
        id: run.account.id,
        email: run.account.email,
        label: run.account.label,
        ok: run.ok !== false,
        reason: run.ok === false ? "partial" : "ok",
        totalGranted: run.totalGranted || 0,
        points0: run.points0 ?? null,
        points1: run.points1 ?? null,
        results: run.results || [],
      },
    ];
  }
  if (Array.isArray(run.results)) {
    return [
      {
        id: null,
        email: null,
        label: "（历史记录）",
        ok: run.ok !== false,
        reason: run.ok === false ? "partial" : "ok",
        totalGranted: run.totalGranted || 0,
        points0: run.points0 ?? null,
        points1: run.points1 ?? null,
        results: run.results,
      },
    ];
  }
  return [];
}

function checkinVerdict(results) {
  let checkin = "none";
  let last = null;
  for (const r of results || []) {
    if (r.questId !== "daily_checkin") continue;
    if (r.kind === "granted") checkin = "granted";
    else last = r.kind;
  }
  if (checkin !== "granted" && last && ["duplicate", "rejected", "noop", "error"].includes(last)) {
    checkin = last;
  }
  return checkin;
}

function buildToday(runs, today) {
  const todays = runs.filter((r) => r.date === today);
  const byAccount = new Map();

  for (const run of todays) {
    for (const a of normalizeRunAccounts(run)) {
      const key = a.id || "__unknown__";
      const cur = byAccount.get(key) || {
        id: a.id,
        email: a.email,
        label: a.label,
        granted: 0,
        runs: 0,
        checkin: "none",
        lastRunAt: null,
        points0: null,
        points1: null,
        verdicts: [],
      };
      cur.runs += 1;
      cur.granted = Math.round((cur.granted + (a.totalGranted || 0)) * 100) / 100;
      cur.verdicts.push(...(a.results || []));
      cur.lastRunAt = run.at || cur.lastRunAt;
      if (typeof a.points0 === "number") cur.points0 = a.points0;
      if (typeof a.points1 === "number") cur.points1 = a.points1;
      byAccount.set(key, cur);
    }
  }

  for (const v of byAccount.values()) {
    v.checkin = checkinVerdict(v.verdicts);
    delete v.verdicts;
  }

  const totalGranted = Math.round(
    [...byAccount.values()].reduce((s, a) => s + (a.granted || 0), 0) * 100,
  ) / 100;
  const lastRun = todays[todays.length - 1] || null;

  return { date: today, runs: todays.length, totalGranted, byAccount, lastRunAt: lastRun?.at || null };
}

async function buildStatus() {
  migrateLegacy();
  const cfg = loadConfig();
  const today = shanghaiDate();
  const runs = readRuns(400);
  const todayInfo = buildToday(runs, today);

  const stored = listAccounts();
  const accounts = [];
  for (const a of stored) {
    const probe = await probeAccount(a);
    const attr = todayInfo.byAccount.get(a.id) || null;
    accounts.push({
      id: a.id,
      email: a.email,
      label: a.label,
      enabled: a.enabled,
      needsLogin: a.needsLogin || !probe.ok,
      lastError: a.lastError || (probe.ok ? "" : probe.reason),
      savedAt: a.savedAt,
      points: probe.points,
      sessionOk: probe.ok,
      sessionError: probe.reason || "",
      today: attr
        ? {
            checkin: attr.checkin,
            granted: attr.granted,
            runs: attr.runs,
            lastRunAt: attr.lastRunAt,
            points0: attr.points0,
            points1: attr.points1,
          }
        : { checkin: "none", granted: 0, runs: 0, lastRunAt: null, points0: null, points1: null },
    });
  }

  return {
    now: new Date().toISOString(),
    clock: shanghaiClock(),
    today,
    accounts,
    summary: {
      total: accounts.length,
      enabled: accounts.filter((a) => a.enabled).length,
      needLogin: accounts.filter((a) => a.needsLogin).length,
      totalPoints: accounts.reduce((s, a) => s + (a.points || 0), 0),
    },
    config: cfg,
    catalog: QUEST_CATALOG,
    todaySummary: {
      date: today,
      runs: todayInfo.runs,
      totalGranted: todayInfo.totalGranted,
      lastRunAt: todayInfo.lastRunAt,
    },
    recent: runs.slice(-60).map((r) => ({
      at: r.at,
      date: r.date,
      ok: r.ok,
      totalGranted: r.totalGranted || 0,
      accounts: normalizeRunAccounts(r).map((a) => ({
        id: a.id,
        email: a.email,
        label: a.label,
        ok: a.ok,
        reason: a.reason,
        totalGranted: a.totalGranted || 0,
        points0: a.points0 ?? null,
        points1: a.points1 ?? null,
      })),
    })),
    port: PORT,
  };
}

// ------------------------------------------------------------------ 打开浏览器

/**
 * 用默认浏览器打开控制台。
 * 注意：这件事必须由 Node 来做，不能由桌面 exe 直接做 ——
 * 实测从 GUI 子系统进程里 CreateProcess 启动 Edge，冷启动会以 exitCode 21 立即退出
 * （窗口不出现），而从 Node 里 spawn 同一路径同一参数则正常。所以统一交给这里。
 */
function findEdge() {
  const candidates = [
    "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
    path.join(process.env.LOCALAPPDATA || "", "Microsoft\\Edge\\Application\\msedge.exe"),
  ];
  for (const p of candidates) {
    try {
      if (p && fs.existsSync(p)) return p;
    } catch {
      /* ignore */
    }
  }
  return null;
}

function openLog(line) {
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    fs.appendFileSync(
      path.join(LOG_DIR, "open-browser.log"),
      `[${new Date().toISOString()}] ${line}\\n`,
      "utf8",
    );
  } catch {
    /* 日志失败不影响打开 */
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
/**
 * 打开浏览器，并确认页面真的渲染出来了。
 *
 * 只看进程有没有起来是不够的，这台机器上踩过两个坑：
 *  1) 默认配置目录：Edge 报 "Lock file can not be created! Error code: 5"，
 *     单实例锁建不起来就直接退出 → 窗口根本不出现。用独立 --user-data-dir 解决。
 *  2) 某些受限环境里 renderer 进程起不来 → 窗口出来了但白屏。
 *     加 --no-sandbox 能恢复，但 Edge 会顶一条警告条，所以只在真白屏时才降级。
 *
 * 判断"渲染出来了"用页面回调：打开时带 ?w=<token>，ui.html 加载后回 /api/hello。
 */
async function openBrowser(target) {
  const cfg = loadConfig();
  const allowNoSandbox = cfg.edgeNoSandbox !== false;
  const edge = findEdge();
  const attempts = [];

  if (!edge) {
    openLog("找不到 msedge.exe，改用 cmd start");
    attempts.push(openViaShell(target));
    return attempts;
  }

  const profileDir = path.join(ROOT, "browser-profile-ui");
  try {
    fs.mkdirSync(profileDir, { recursive: true });
  } catch {
    /* 建不起来就让 Edge 自己报错 */
  }

  // 窗口形态：app = 无地址栏的独立窗口（像原生应用），tab = 普通标签页
  const mode = cfg.windowMode === "tab" ? "tab" : "app";
  const baseArgs = [
    `--user-data-dir=${profileDir}`,
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-sync",
    "--window-size=1400,950",
  ];

  // 上次哪种方式成功过，这次就先用哪种，避免每次都要先白屏等一圈（冷启动能从 11 秒降到 2 秒左右）
  const remembered = readUiState();
  const order = [];
  if (remembered && typeof remembered.noSandbox === "boolean") {
    order.push(remembered.noSandbox);
    if (allowNoSandbox) order.push(!remembered.noSandbox);
  } else {
    order.push(false);
    if (allowNoSandbox) order.push(true);
  }

  for (const noSandbox of [...new Set(order)]) {
    if (noSandbox && !allowNoSandbox) continue;

    // 换方式之前先清掉占用这个配置目录的 Edge 实例
    if (attempts.length > 0) {
      await killProfileEdge();
      await sleep(1200);
    }

    const args = noSandbox ? [...baseArgs, "--no-sandbox", "--test-type"] : [...baseArgs];
    const label = noSandbox ? "edge-no-sandbox" : "edge";
    const result = await launchEdge(edge, args, label, target, mode);
    attempts.push(result);

    if (result.loaded) {
      writeUiState({ noSandbox, at: new Date().toISOString() });
      return attempts;
    }
  }

  attempts.push(openViaShell(target));
  return attempts;
}

/** 记住上次成功打开界面的方式，下次直接用它 */
function readUiState() {
  try {
    return JSON.parse(fs.readFileSync(UI_STATE_FILE, "utf8"));
  } catch {
    return null;
  }
}

function writeUiState(state) {
  try {
    fs.writeFileSync(UI_STATE_FILE, JSON.stringify(state, null, 2), "utf8");
  } catch {
    /* 记不住也不影响功能 */
  }
}

/**
 * 启动一次 Edge 并等待页面回调。
 *
 * 注意：**不能因为子进程提前退出就判定失败**。
 * 当这个配置目录下已经有 Edge 在运行时，新进程会把请求交给已有实例然后自己退出（exit 0），
 * 这是正常行为，新标签页照样会打开并回调。所以这里只认"有没有等到 hello"。
 */
async function launchEdge(edge, baseArgs, label, target, mode = "app") {
  const token = crypto.randomUUID().replace(/-/g, "");
  const url = `${target}?w=${token}`;
  // app 模式不显示地址栏与标签栏，看起来就是一个独立应用窗口
  const finalArgs = mode === "tab" ? [...baseArgs, url] : [...baseArgs, `--app=${url}`];

  let child = null;
  let stderr = "";
  try {
    child = spawn(edge, finalArgs, {
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: false,
    });
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (d) => {
      if (stderr.length < 4000) stderr += d;
    });
  } catch (err) {
    openLog(`${label} spawn error: ${err?.message || err}`);
    return { method: label, ok: false, error: err?.message || String(err), loaded: false };
  }

  const loaded = await waitForHello(token, 9000);
  const exited = child.exitCode !== null;

  if (loaded) {
    try {
      child.unref();
    } catch {
      /* ignore */
    }
    openLog(`${label} loaded=true pid=${child.pid}`);
  } else {
    openLog(
      `${label} loaded=false exited=${exited}` +
        (exited ? ` code=${child.exitCode}` : "") +
        ` stderr=${JSON.stringify(stderr.slice(-400))}`,
    );
  }

  return { method: label, ok: true, loaded, pid: child.pid };
}

/** 关掉占用 browser-profile-ui 的 Edge 进程（不会碰用户日常用的浏览器） */
function killProfileEdge() {
  return new Promise((resolve) => {
    const script =
      "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | " +
      "Where-Object { $_.CommandLine -like '*browser-profile-ui*' } | " +
      "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
    try {
      const p = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
        stdio: "ignore",
        windowsHide: true,
      });
      p.on("exit", () => resolve());
      p.on("error", () => resolve());
    } catch {
      resolve();
    }
  });
}

function openViaShell(target) {
  try {
    const child = spawn("cmd.exe", ["/c", "start", "", target], {
      detached: true,
      stdio: "ignore",
      windowsHide: true,
    });
    child.unref();
    openLog(`cmd-start pid=${child.pid}`);
    return { method: "cmd-start", ok: true, loaded: null };
  } catch (err) {
    openLog(`cmd-start error: ${err?.message || err}`);
    return { method: "cmd-start", ok: false, error: err?.message || String(err), loaded: false };
  }
}

function waitForHello(token, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return (async () => {
    while (Date.now() < deadline) {
      if (helloSeen.has(token)) return true;
      await sleep(300);
    }
    return false;
  })();
}

// ------------------------------------------------------------------ 运行

let running = false;
const helloSeen = new Map();

function runCheckin(res, accountId) {
  // 不通过管道拿子进程输出：子进程自己写 logs/<date>.log，这里增量 tail 该文件。
  // 好处是断线不丢日志、可回放，也不依赖管道（部分受限环境会拒绝管道）。
  const logFile = path.join(LOG_DIR, `${shanghaiDate()}.log`);
  let offset = 0;
  try {
    offset = fs.existsSync(logFile) ? fs.statSync(logFile).size : 0;
  } catch {
    offset = 0;
  }

  const args = [CHECKIN];
  if (accountId) args.push("--account", accountId);

  let child;
  try {
    child = spawn(process.execPath, args, {
      cwd: ROOT,
      env: { ...process.env, FORCE_COLOR: "0", NO_COLOR: "1" },
      windowsHide: true,
      stdio: "ignore",
    });
  } catch (err) {
    running = false;
    return json(res, 500, { error: `无法启动签到进程：${err.message}` });
  }

  res.writeHead(200, {
    "content-type": "application/x-ndjson; charset=utf-8",
    "cache-control": "no-store",
    connection: "keep-alive",
  });

  let finished = false;
  let pending = "";

  const send = (obj) => {
    try {
      if (!res.writableEnded) res.write(JSON.stringify(obj) + "\n");
    } catch {
      /* 客户端可能已断开，忽略 */
    }
  };

  const drain = () => {
    let size = 0;
    try {
      size = fs.existsSync(logFile) ? fs.statSync(logFile).size : 0;
    } catch {
      return;
    }
    if (size <= offset) return;
    let text = "";
    try {
      const fd = fs.openSync(logFile, "r");
      const len = size - offset;
      const buf = Buffer.alloc(len);
      fs.readSync(fd, buf, 0, len, offset);
      fs.closeSync(fd);
      text = buf.toString("utf8");
      offset = size;
    } catch {
      return;
    }
    pending += text;
    const lines = pending.split(/\r?\n/);
    pending = lines.pop() || "";
    for (const line of lines) if (line.trim()) send({ type: "line", text: line });
  };

  const ticker = setInterval(drain, 300);

  const finish = (code) => {
    if (finished) return;
    finished = true;
    clearInterval(ticker);
    clearTimeout(killer);
    drain();
    if (pending.trim()) send({ type: "line", text: pending });
    send({ type: "done", code });
    running = false;
    sessionCache.clear();
    try {
      res.end();
    } catch {
      /* ignore */
    }
  };

  const killer = setTimeout(() => {
    send({ type: "line", text: "[控制台] 执行超时（120 秒），已中止" });
    try {
      child.kill();
    } catch {
      /* ignore */
    }
    finish(124);
  }, 120000);

  child.on("error", (err) => {
    send({ type: "line", text: `[控制台] 启动失败：${err.message}` });
    finish(1);
  });
  child.on("close", (code) => finish(code ?? 1));
}

// ------------------------------------------------------------------ 路由

async function handle(req, res) {
  const url = new URL(req.url, `http://${HOST}:${PORT}`);
  const pathname = url.pathname;
  const method = req.method || "GET";

  if (pathname === "/api/ping") return json(res, 200, { ok: true, port: PORT });

  // 桌面客户端（托盘菜单 / 重复启动）让服务端去开浏览器。
  // openBrowser 内部会带一次性 token 等页面回调，以此判断"窗口里真的出画面了"。
  if (pathname === "/api/open" && method === "POST") {
    const target = `http://127.0.0.1:${PORT}/`;
    const attempts = await openBrowser(target);
    const spawned = attempts.some((a) => a.ok);
    const loaded = attempts.some((a) => a.loaded === true);

    console.log("[console] 打开浏览器：", JSON.stringify({ spawned, loaded, attempts }));
    return json(res, spawned ? 200 : 500, { ok: spawned, loaded, attempts, url: target });
  }

  // 页面加载握手（启动器用，保留兼容）
  if (pathname === "/api/hello") {
    const token = url.searchParams.get("t") || "";
    const check = url.searchParams.get("check") === "1";
    if (!check) {
      helloSeen.set(token, Date.now());
      if (helloSeen.size > 20) {
        const oldest = [...helloSeen.entries()].sort((a, b) => a[1] - b[1])[0];
        if (oldest) helloSeen.delete(oldest[0]);
      }
      return json(res, 200, { ok: true });
    }
    return json(res, 200, { ok: true, seen: token ? helloSeen.has(token) : false });
  }

  if (pathname === "/api/status") return json(res, 200, await buildStatus());

  if (pathname === "/api/runs") {
    const limit = Math.min(500, Math.max(1, Number(url.searchParams.get("limit")) || 100));
    return json(res, 200, {
      runs: readRuns(limit).map((r) => ({
        ...r,
        accounts: normalizeRunAccounts(r),
      })),
    });
  }

  if (pathname === "/api/logs") {
    const today = shanghaiDate();
    const file = path.join(LOG_DIR, `${today}.log`);
    let lines = [];
    if (fs.existsSync(file)) lines = fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean);
    return json(res, 200, { date: today, lines: lines.slice(-300) });
  }

  if (pathname === "/api/export") {
    if (!fs.existsSync(RUNS_FILE)) {
      res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
      return res.end("暂无执行历史");
    }
    res.writeHead(200, {
      "content-type": "application/x-ndjson; charset=utf-8",
      "content-disposition": `attachment; filename="ans-runs-${shanghaiDate()}.jsonl"`,
    });
    return fs.createReadStream(RUNS_FILE).pipe(res);
  }

  if (pathname === "/api/run" && method === "POST") {
    if (running) return json(res, 409, { error: "已有任务在执行中" });
    running = true;
    return runCheckin(res, url.searchParams.get("account") || "");
  }

  if (pathname === "/api/login" && method === "POST") {
    try {
      const child = spawn(process.execPath, [GRAB], {
        cwd: ROOT,
        detached: true,
        stdio: "ignore",
        windowsHide: false,
      });
      child.unref();
      sessionCache.clear();
      return json(res, 200, { ok: true, message: "已打开登录窗口，请在弹出的浏览器里登录要添加的账号" });
    } catch (err) {
      return json(res, 500, { error: `无法启动登录窗口：${err.message}` });
    }
  }

  // 方式二：账号密码登录
  if (pathname === "/api/login/password" && method === "POST") {
    let body;
    try {
      body = JSON.parse(await readBody(req));
    } catch {
      return json(res, 400, { error: "请求体不是合法 JSON" });
    }
    const email = String(body.email || "").trim();
    const password = String(body.password || "");
    if (!email || !password) return json(res, 400, { error: "请填写邮箱和密码" });

    const result = await loginWithPassword(email, password);
    if (!result.ok) {
      return json(res, 401, { error: result.reason, code: result.code });
    }

    const existing = listAccounts().find((a) => a.id === result.user.id);
    saveAccount({
      id: result.user.id,
      email: result.user.email || email,
      label: result.user.name || result.user.email || email,
      cookie: result.cookie,
      savedAt: new Date().toISOString(),
      enabled: true,
      needsLogin: false,
      lastError: "",
      loginMethod: "password",
    });
    sessionCache.delete(result.user.id);

    return json(res, 200, {
      ok: true,
      created: !existing,
      account: {
        id: result.user.id,
        email: result.user.email || email,
        label: result.user.name || result.user.email || email,
      },
      points: result.points ?? null,
    });
  }

  // 方式三：直接粘贴 Cookie
  if (pathname === "/api/import/cookie" && method === "POST") {
    let body;
    try {
      body = JSON.parse(await readBody(req));
    } catch {
      return json(res, 400, { error: "请求体不是合法 JSON" });
    }
    const raw = String(body.cookie || "");
    if (!raw.trim()) return json(res, 400, { error: "请粘贴 Cookie" });

    const check = await validateCookie(raw);
    if (!check.ok) return json(res, 400, { error: check.reason || "Cookie 无效" });

    const existing = listAccounts().find((a) => a.id === check.user.id);
    saveAccount({
      id: check.user.id,
      email: check.user.email || existing?.email || "",
      label: check.user.name || existing?.label || check.user.email || check.user.id,
      cookie: check.cookie,
      savedAt: new Date().toISOString(),
      enabled: true,
      needsLogin: false,
      lastError: "",
      loginMethod: "cookie",
    });
    sessionCache.delete(check.user.id);

    return json(res, 200, {
      ok: true,
      created: !existing,
      account: {
        id: check.user.id,
        email: check.user.email || "",
        label: check.user.name || check.user.email || check.user.id,
      },
      points: check.points ?? null,
    });
  }

  // 账号操作
  const accMatch = pathname.match(/^\/api\/accounts\/([A-Za-z0-9_-]+)$/);
  if (accMatch) {
    const id = accMatch[1];
    if (!getAccount(id)) return json(res, 404, { error: "账号不存在" });

    if (method === "DELETE") {
      deleteAccount(id);
      sessionCache.delete(id);
      return json(res, 200, { ok: true });
    }
    if (method === "PATCH") {
      let body;
      try {
        body = JSON.parse(await readBody(req));
      } catch {
        return json(res, 400, { error: "请求体不是合法 JSON" });
      }
      const patch = {};
      if (typeof body.enabled === "boolean") patch.enabled = body.enabled;
      if (typeof body.label === "string") patch.label = body.label.slice(0, 60);
      patchAccount(id, patch);
      return json(res, 200, { ok: true, patch });
    }
    return json(res, 405, { error: "不支持的方法" });
  }

  if (pathname === "/api/accounts" && method === "GET") {
    return json(res, 200, { accounts: listAccounts().map((a) => ({ ...a, cookie: undefined })) });
  }

  if (pathname === "/api/config" && method === "POST") {
    let body;
    try {
      body = JSON.parse(await readBody(req));
    } catch {
      return json(res, 400, { error: "请求体不是合法 JSON" });
    }
    const cfg = loadConfig();
    if (Array.isArray(body.quests)) {
      const valid = new Set(QUEST_CATALOG.map((q) => q.id));
      const cleaned = body.quests.filter((q) => valid.has(q));
      if (cleaned.length === 0) return json(res, 400, { error: "至少要保留一个任务" });
      cfg.quests = cleaned;
    }
    if (typeof body.webhook === "string") cfg.webhook = body.webhook.slice(0, 500);
    writeJson(CONFIG_FILE, cfg);
    return json(res, 200, { ok: true, config: cfg });
  }

  if (pathname === "/" || pathname === "/index.html") {
    if (!fs.existsSync(UI_FILE)) {
      res.writeHead(500, { "content-type": "text/plain; charset=utf-8" });
      return res.end("缺少 ui.html");
    }
    const html = await fsp.readFile(UI_FILE, "utf8");
    res.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
    return res.end(html);
  }

  res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
  res.end("Not Found");
}

// ------------------------------------------------------------------ 启动

let PORT = DEFAULT_PORT;

function startServer(port, attempt = 0) {
  const handler = (req, res) => {
    handle(req, res).catch((err) => {
      console.error("[console] 请求处理失败：", err);
      if (!res.headersSent) json(res, 500, { error: "内部错误" });
      else res.end();
    });
  };

  const server = http.createServer(handler);

  server.on("error", (err) => {
    if (err.code === "EADDRINUSE" && attempt < 10) {
      server.close();
      return startServer(port + 1, attempt + 1);
    }
    console.error("[console] 服务启动失败：", err.message);
    process.exit(1);
  });

  server.listen(port, HOST, () => {
    PORT = port;
    try {
      fs.writeFileSync(path.join(ROOT, ".console-port"), String(port), "utf8");
    } catch {
      /* 端口文件仅用于桌面启动器定位，失败不影响使用 */
    }
    console.log(`[console] ANS 签到控制台已启动：http://${HOST}:${port}`);

    // 额外监听 IPv6 回环。浏览器访问 localhost 时可能先解析到 ::1，
    // 若这里不监听，页面会一直空白（实测 Edge 不会马上回退到 IPv4）。
    try {
      const v6 = http.createServer(handler);
      v6.on("error", () => {
        /* 端口被 IPv6 占用等情况直接忽略，不影响 IPv4 正常使用 */
      });
      v6.listen(port, "::1", () => console.log(`[console] 同时监听 [::1]:${port}`));
    } catch {
      /* ignore */
    }

    // 桌面客户端以 auto 模式（--no-open）启动时不弹浏览器，只常驻托盘
    const noOpen = process.argv.includes("--no-open") || process.env.ANS_NO_OPEN === "1";
    if (!noOpen) {
      openBrowser(`http://127.0.0.1:${port}/`)
        .then((attempts) => console.log("[console] 打开浏览器结果：", JSON.stringify(attempts)))
        .catch((err) => console.log("[console] 打开浏览器异常：", err?.message || err));
    } else {
      console.log(`[console] 已跳过自动打开浏览器（--no-open），访问 http://127.0.0.1:${port}/`);
    }
  });
}

startServer(DEFAULT_PORT);

for (const sig of ["SIGINT", "SIGTERM"]) {
  process.on(sig, () => {
    console.log("\n[console] 正在退出…");
    process.exit(0);
  });
}

