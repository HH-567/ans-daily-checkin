#!/usr/bin/env node
/**
 * ANS 工作台 · 多账号每日签到与日常任务自动执行
 *
 * 原理：站点把"签到/日常任务"的最终记账全部交给服务端接口
 *       POST /api/compute-benefits/arcade  {kind:"quest", questId}
 *       前端 localStorage 只是缓存展示，本身不产生积分。
 *       所以只要带上登录态 Cookie 直接调用该接口即可，无需浏览器。
 *
 * 账号存放在 accounts/ 下，一个账号一个文件，可同时跑多个账号。
 *
 * 退出码：
 *   0 = 全部账号都成功 / 今日已领过
 *   1 = 配置或未预期错误
 *   2 = 有账号登录态失效，需要重新登录
 *   3 = 有任务失败（网络/接口异常）
 *   4 = 服务端拒绝（COMPUTE_BENEFITS_FAILED，站点自身点击同样失败）
 */
import fs from "node:fs";
import path from "node:path";
import {
  ROOT,
  listAccounts,
  resolveAccount,
  patchAccount,
  migrateLegacy,
  writeAuthMarker,
} from "./lib/accounts.mjs";

const ORIGIN = "https://ans.cauai.fun";
const CONFIG_FILE = path.join(ROOT, "config.json");
const LOG_DIR = path.join(ROOT, "logs");

/** 任务目录（与站点前端 QUEST_CATALOG 对齐） */
const QUEST_CATALOG = {
  daily_checkin: { zh: "每日算力签到", reward: "+5 积分 +1 抓娃娃币" },
  daily_vibehub_read: { zh: "VibeHub 沉浸充电 60 秒", reward: "+5 积分 +1 币" },
  daily_explore_star: { zh: "星火助威 · 点亮 Star", reward: "+5 积分" },
  daily_agent_feed: { zh: "小恐龙特训 · 投喂/对话", reward: "+3 积分" },
};

const DEFAULT_CONFIG = {
  quests: ["daily_checkin", "daily_vibehub_read", "daily_explore_star", "daily_agent_feed"],
  retries: 3,
  retryDelayMs: 2500,
  timeoutMs: 20000,
  webhook: "",
};

// ---------------------------------------------------------------- 工具函数

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function shanghaiDate(d = new Date()) {
  try {
    return new Intl.DateTimeFormat("en-CA", {
      timeZone: "Asia/Shanghai",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(d);
  } catch {
    return new Date(d.getTime() + 8 * 3600_000).toISOString().slice(0, 10);
  }
}

function shanghaiTime(d = new Date()) {
  try {
    return new Intl.DateTimeFormat("sv-SE", {
      timeZone: "Asia/Shanghai",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false,
    }).format(d);
  } catch {
    return d.toISOString();
  }
}

function readJson(file, fallback = null) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return fallback;
  }
}

function loadConfig() {
  const user = readJson(CONFIG_FILE, {});
  const cfg = { ...DEFAULT_CONFIG, ...(user || {}) };
  if (!Array.isArray(cfg.quests) || cfg.quests.length === 0) cfg.quests = DEFAULT_CONFIG.quests;
  return cfg;
}

function round2(n) {
  return Math.round(n * 100) / 100;
}

// ---------------------------------------------------------------- HTTP

let CURRENT_COOKIE = "";

async function api(pathname, { method = "GET", body, timeoutMs = 20000 } = {}) {
  const headers = {
    accept: "application/json, text/plain, */*",
    "accept-language": "zh-CN,zh;q=0.9",
    cookie: CURRENT_COOKIE,
    origin: ORIGIN,
    referer: `${ORIGIN}/workspace/benefits`,
    "user-agent":
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
    "sec-fetch-site": "same-origin",
    "sec-fetch-mode": "cors",
    "sec-fetch-dest": "empty",
  };
  if (body !== undefined) headers["content-type"] = "application/json";

  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs);
  try {
    const res = await fetch(ORIGIN + pathname, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ac.signal,
      redirect: "manual",
    });
    const text = await res.text();
    let json = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {
      /* 非 JSON 响应（多为 HTML 错误页） */
    }
    return { status: res.status, json, text };
  } finally {
    clearTimeout(timer);
  }
}

// ---------------------------------------------------------------- 日志

function ensureLogDir() {
  fs.mkdirSync(LOG_DIR, { recursive: true });
}

function logLine(line) {
  const stamped = `[${shanghaiTime()}] ${line}`;
  console.log(stamped);
  try {
    ensureLogDir();
    fs.appendFileSync(path.join(LOG_DIR, `${shanghaiDate()}.log`), stamped + "\r\n", "utf8");
  } catch {
    /* 日志失败不影响主流程 */
  }
}

function logEvent(event) {
  try {
    ensureLogDir();
    fs.appendFileSync(
      path.join(LOG_DIR, "runs.jsonl"),
      JSON.stringify({ at: new Date().toISOString(), date: shanghaiDate(), ...event }) + "\n",
      "utf8",
    );
  } catch {
    /* ignore */
  }
}

// ---------------------------------------------------------------- 登录态

async function verifySession() {
  const sess = await api("/api/auth/session");
  if (sess.status === 401 || (sess.status === 200 && sess.json == null)) {
    return { ok: false, reason: "not-logged-in", detail: "会话无效或已过期" };
  }
  if (sess.status !== 200 || !sess.json) {
    return { ok: false, reason: "unexpected", detail: `session HTTP ${sess.status}` };
  }

  const points = await api("/api/me/points");
  if (points.status === 401) {
    return { ok: false, reason: "not-logged-in", detail: "积分接口返回 401" };
  }
  if (points.status !== 200) {
    return { ok: false, reason: "unexpected", detail: `points HTTP ${points.status}` };
  }

  const user = sess.json?.user ?? sess.json;
  return {
    ok: true,
    user: {
      id: user?.id ?? null,
      name: user?.name ?? null,
      email: user?.email ?? null,
    },
    points: points.json,
  };
}

/** /api/me/points 返回结构可能是 {points}/{balance}/{data:{...}}，做兼容提取 */
function extractPoints(payload) {
  if (payload == null) return null;
  const candidates = [
    payload.points,
    payload.balance,
    payload.balancePoints,
    payload.data?.points,
    payload.data?.balance,
  ];
  for (const c of candidates) {
    if (typeof c === "number" && Number.isFinite(c)) return round2(c);
  }
  return null;
}

// ---------------------------------------------------------------- 任务提交

function classify(res) {
  const j = res.json || {};
  if (res.status === 401) return { kind: "auth" };
  if (res.status >= 200 && res.status < 300) {
    if (j.duplicate) return { kind: "duplicate", granted: 0, balance: j.balanceAfter };
    const granted = Number(j.grantedPoints ?? 0);
    return { kind: granted > 0 ? "granted" : "noop", granted, balance: j.balanceAfter };
  }
  // 站点对"当日已记录"的签到会直接抛 500，而不是返回 duplicate。
  // 站点自己的前端点击也是同样的结果（已实测），属于服务端缺陷，重试无意义。
  if (j.error === "COMPUTE_BENEFITS_FAILED") {
    return { kind: "rejected", errorCode: j.error };
  }
  return { kind: "error", errorCode: j.error || `HTTP_${res.status}` };
}

async function claimQuest(questId, cfg) {
  const attempts = Math.max(1, Number(cfg.retries) || 1);
  let last = null;

  for (let i = 1; i <= attempts; i++) {
    let res;
    try {
      res = await api("/api/compute-benefits/arcade", {
        method: "POST",
        body: { kind: "quest", questId },
        timeoutMs: cfg.timeoutMs,
      });
    } catch (err) {
      last = { kind: "network", detail: err?.message || String(err) };
      if (i < attempts) {
        logLine(`  ↻ 第 ${i} 次请求异常（${last.detail}），${cfg.retryDelayMs}ms 后重试`);
        await sleep(cfg.retryDelayMs);
        continue;
      }
      return last;
    }

    const verdict = classify(res);
    if (verdict.kind === "auth") return verdict;
    if (verdict.kind === "error") {
      last = { ...verdict, detail: res.json?.message || res.text?.slice(0, 200) };
      const retriable = res.status >= 500 || res.status === 429;
      if (retriable && i < attempts) {
        logLine(`  ↻ 第 ${i} 次失败（${last.errorCode}），${cfg.retryDelayMs}ms 后重试`);
        await sleep(cfg.retryDelayMs * i);
        continue;
      }
      return last;
    }
    return verdict;
  }
  return last || { kind: "error", errorCode: "UNKNOWN" };
}

// ---------------------------------------------------------------- 通知

async function sendWebhook(cfg, payload) {
  if (!cfg.webhook) return;
  try {
    const ac = new AbortController();
    const t = setTimeout(() => ac.abort(), 10000);
    await fetch(cfg.webhook, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(payload),
      signal: ac.signal,
    });
    clearTimeout(t);
  } catch (err) {
    logLine(`! 通知推送失败：${err?.message || err}`);
  }
}

// ---------------------------------------------------------------- 单账号执行

function accountLabel(a) {
  return a.email || a.label || a.id;
}

async function runAccount(account, cfg, opts) {
  CURRENT_COOKIE = account.cookie;
  const label = accountLabel(account);
  logLine(`── ${label} ──`);

  const auth = await verifySession();
  if (!auth.ok) {
    logLine(`  ✕ 登录态无效：${auth.detail}`);
    patchAccount(account.id, {
      needsLogin: true,
      lastError: auth.detail,
      lastCheckedAt: new Date().toISOString(),
    });
    return {
      account: { id: account.id, email: account.email, label: account.label },
      ok: false,
      reason: auth.reason,
      detail: auth.detail,
      results: [],
      totalGranted: 0,
      points0: null,
      points1: null,
    };
  }

  patchAccount(account.id, {
    needsLogin: false,
    lastError: "",
    lastCheckedAt: new Date().toISOString(),
    email: auth.user?.email || account.email,
    label: auth.user?.name || account.label,
  });

  const points0 = extractPoints(auth.points);
  logLine(`  已登录，当前积分：${points0 ?? "—"}`);

  if (opts.checkOnly) {
    return {
      account: { id: account.id, email: account.email, label: account.label },
      ok: true,
      reason: "check",
      results: [],
      totalGranted: 0,
      points0,
      points1: points0,
    };
  }

  const results = [];
  for (const questId of cfg.quests) {
    const meta = QUEST_CATALOG[questId] || { zh: questId };
    const r = await claimQuest(questId, cfg);

    let line;
    switch (r.kind) {
      case "granted":
        line = `  ✓ ${meta.zh} → +${r.granted} 积分${r.balance != null ? `（余额 ${r.balance}）` : ""}`;
        break;
      case "duplicate":
        line = `  = ${meta.zh} → 今日已领过`;
        break;
      case "rejected":
        line = `  ! ${meta.zh} → 服务端拒绝（COMPUTE_BENEFITS_FAILED），站点自身点击同样失败`;
        break;
      case "noop":
        line = `  – ${meta.zh} → 服务端未增加积分`;
        break;
      case "network":
        line = `  ✕ ${meta.zh} → 网络异常：${r.detail}`;
        break;
      default:
        line = `  ✕ ${meta.zh} → 失败：${r.errorCode}${r.detail ? ` ${r.detail}` : ""}`;
    }
    logLine(line);
    results.push({ questId, ...r });
    if (r.kind !== "duplicate") await sleep(1200);
  }

  const after = await verifySession();
  const points1 = after.ok ? extractPoints(after.points) : null;
  if (points0 != null && points1 != null) {
    logLine(`  积分：${points0} → ${points1}（+${round2(points1 - points0)}）`);
  }

  const granted = results.filter((r) => r.kind === "granted");
  const failed = results.filter((r) => ["error", "network"].includes(r.kind));
  const rejected = results.filter((r) => r.kind === "rejected");
  const totalGranted = round2(granted.reduce((s, r) => s + (r.granted || 0), 0));
  const authLost = results.some((r) => r.kind === "auth");

  if (authLost) {
    patchAccount(account.id, { needsLogin: true, lastError: "执行过程中会话失效" });
  }

  return {
    account: { id: account.id, email: account.email, label: account.label },
    ok: failed.length === 0 && !authLost,
    reason: authLost ? "auth-lost" : failed.length ? "partial" : "ok",
    results,
    totalGranted,
    points0,
    points1,
    rejectedCount: rejected.length,
    failedCount: failed.length,
  };
}

// ---------------------------------------------------------------- 主流程

function parseArgs(argv) {
  const opts = { checkOnly: false, dryRun: false, account: "", help: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--check") opts.checkOnly = true;
    else if (a === "--dry-run") opts.dryRun = true;
    else if (a === "--account" || a === "-a") opts.account = argv[++i] || "";
    else if (a === "--help" || a === "-h") opts.help = true;
  }
  return opts;
}

function printHelp() {
  console.log(`
用法：node checkin.mjs [选项]

  (无参数)              依次执行所有已添加账号
  --account <账号>      只执行指定账号（支持 id / 邮箱 / 昵称，可模糊匹配）
  --check               只检查各账号登录态与积分，不领取任何任务
  --dry-run             打印将要提交的任务，不发请求
  -h, --help            显示帮助
`);
}

async function main() {
  const opts = parseArgs(process.argv);
  if (opts.help) {
    printHelp();
    return 0;
  }

  migrateLegacy();
  const cfg = loadConfig();

  let accounts = listAccounts().filter((a) => a.enabled !== false);
  if (opts.account) {
    const one = resolveAccount(opts.account);
    if (!one) {
      logLine(`✕ 找不到账号：${opts.account}`);
      return 1;
    }
    accounts = [one];
  }

  logLine(`=== ANS 每日签到 · ${shanghaiDate()} · ${accounts.length} 个账号 ===`);

  if (accounts.length === 0) {
    logLine("✕ 还没有添加账号。双击 导入Cookie.bat，或在控制台点「添加账号」。");
    writeAuthMarker([]);
    logEvent({ ok: false, reason: "no-account" });
    return 2;
  }

  if (opts.dryRun) {
    logLine("DRY-RUN：不发请求");
    for (const a of accounts) logLine(`  · ${accountLabel(a)}`);
    for (const q of cfg.quests) {
      const meta = QUEST_CATALOG[q] || { zh: q, reward: "" };
      logLine(`  · ${q}  ${meta.zh}  (${meta.reward})`);
    }
    return 0;
  }

  const summaries = [];
  for (const account of accounts) {
    try {
      summaries.push(await runAccount(account, cfg, opts));
    } catch (err) {
      logLine(`  ✕ ${accountLabel(account)} 执行异常：${err?.message || err}`);
      summaries.push({
        account: { id: account.id, email: account.email, label: account.label },
        ok: false,
        reason: "exception",
        detail: String(err?.message || err),
        results: [],
        totalGranted: 0,
      });
    }
    await sleep(600);
  }

  // ---- 汇总
  const totalGranted = round2(summaries.reduce((s, x) => s + (x.totalGranted || 0), 0));
  const expired = summaries.filter((x) => x.reason === "not-logged-in" || x.reason === "auth-lost");
  const failed = summaries.filter((x) => ["partial", "exception", "unexpected"].includes(x.reason));
  const rejected = summaries.filter((x) => (x.rejectedCount || 0) > 0);
  const okCount = summaries.filter((x) => x.ok).length;

  logLine("── 汇总 ──");
  for (const s of summaries) {
    const name = s.account.email || s.account.label || s.account.id;
    const tag = s.ok
      ? s.totalGranted > 0
        ? `+${s.totalGranted} 积分`
        : "无新增"
      : s.reason === "not-logged-in" || s.reason === "auth-lost"
        ? "需要重新登录"
        : "失败";
    logLine(`  ${name}：${tag}`);
  }

  writeAuthMarker(expired.map((s) => s.account));

  const record = {
    ok: failed.length === 0 && expired.length === 0,
    accounts: summaries.map((s) => ({
      id: s.account.id,
      email: s.account.email,
      label: s.account.label,
      ok: s.ok,
      reason: s.reason,
      totalGranted: s.totalGranted || 0,
      points0: s.points0 ?? null,
      points1: s.points1 ?? null,
      results: s.results || [],
    })),
    totalGranted,
    accountCount: summaries.length,
    okCount,
    expired: expired.map((s) => s.account.email || s.account.id),
    failed: failed.map((s) => s.account.email || s.account.id),
  };
  logEvent(record);

  logLine(
    `=== 完成：${summaries.length} 个账号，共领取 ${totalGranted} 积分` +
      `${expired.length ? `，${expired.length} 个需重新登录` : ""}` +
      `${failed.length ? `，${failed.length} 个失败` : ""} ===`,
  );

  if (cfg.webhook) {
    await sendWebhook(cfg, {
      title: expired.length ? "ANS 签到：有账号需要重新登录" : "ANS 签到完成",
      content:
        `${shanghaiDate()} ${summaries.length} 个账号，共 +${totalGranted} 积分` +
        (expired.length ? `，${expired.length} 个登录失效` : "") +
        (failed.length ? `，${failed.length} 个失败` : ""),
    });
  }

  if (failed.length) return 3;
  if (expired.length) return 2;
  if (rejected.length) return 4;
  return 0;
}

main()
  .then((code) => {
    process.exitCode = code;
  })
  .catch((err) => {
    console.error("未预期错误：", err);
    logLine(`✕ 未预期错误：${err?.stack || err}`);
    process.exitCode = 1;
  });
