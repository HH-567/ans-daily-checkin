#!/usr/bin/env node
/**
 * ANS 签到 · Cookie 导入工具
 *
 * 把浏览器里已登录的会话 Cookie 保存到 session.json，供 checkin.mjs 复用。
 * 支持三种输入：交互粘贴、命令行参数、文件。
 *
 *   node tools\import-cookie.mjs
 *   node tools\import-cookie.mjs --cookie "authjs.session-token=..."
 *   node tools\import-cookie.mjs --file cookie.txt
 */
import fs from "node:fs";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";
import { saveAccount, listAccounts } from "../lib/accounts.mjs";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const ORIGIN = "https://ans.cauai.fun";
const AUTH_MARKER = path.join(ROOT, "需要重新登录.txt");

const UA =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

function normalizeCookie(raw) {
  if (!raw) return "";
  let s = String(raw).trim();

  const curlMatch =
    s.match(/--cookie\s+'([^']+)'/) ||
    s.match(/-H\s+['"]cookie:\s*([^'"]+)['"]/i) ||
    s.match(/-b\s+'([^']+)'/);
  if (curlMatch) s = curlMatch[1];

  s = s.replace(/^cookie:\s*/i, "").trim();

  const pairs = [];
  for (const chunk of s.split(";")) {
    const item = chunk.trim();
    if (!item) continue;
    const eq = item.indexOf("=");
    if (eq <= 0) continue;
    pairs.push(`${item.slice(0, eq).trim()}=${item.slice(eq + 1).trim()}`);
  }
  return pairs.join("; ");
}

function cookieNames(cookie) {
  return cookie
    .split(";")
    .map((c) => c.split("=")[0].trim())
    .filter(Boolean);
}

async function api(pathname, cookie) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), 20000);
  try {
    const res = await fetch(ORIGIN + pathname, {
      headers: {
        accept: "application/json, text/plain, */*",
        "accept-language": "zh-CN,zh;q=0.9",
        cookie,
        origin: ORIGIN,
        referer: `${ORIGIN}/workspace/benefits`,
        "user-agent": UA,
        "sec-fetch-site": "same-origin",
        "sec-fetch-mode": "cors",
        "sec-fetch-dest": "empty",
      },
      signal: ac.signal,
    });
    const text = await res.text();
    let json = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {
      /* ignore */
    }
    return { status: res.status, json, text };
  } finally {
    clearTimeout(timer);
  }
}

async function validate(cookie) {
  const sess = await api("/api/auth/session", cookie);
  if (sess.status === 401 || (sess.status === 200 && sess.json == null)) {
    return { ok: false, reason: "Cookie 无效或会话已过期" };
  }
  if (sess.status !== 200 || !sess.json) {
    return { ok: false, reason: `会话接口返回 HTTP ${sess.status}` };
  }

  const pts = await api("/api/me/points", cookie);
  if (pts.status === 401) return { ok: false, reason: "积分接口 401，Cookie 无有效登录态" };
  if (pts.status !== 200) return { ok: false, reason: `积分接口返回 HTTP ${pts.status}` };

  const user = sess.json?.user ?? sess.json;
  return {
    ok: true,
    user: {
      id: user?.id ?? null,
      name: user?.name ?? null,
      email: user?.email ?? null,
    },
    points: pts.json,
  };
}

function extractPoints(p) {
  if (p == null) return null;
  for (const v of [p.points, p.balance, p.balancePoints, p.data?.points, p.data?.balance]) {
    if (typeof v === "number" && Number.isFinite(v)) return v;
  }
  return null;
}

function parseArgs(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--cookie") out.cookie = argv[++i];
    else if (a === "--file") out.file = argv[++i];
    else if (a === "--help" || a === "-h") out.help = true;
  }
  return out;
}

function readStdin() {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: false });
    const parts = [];
    rl.on("line", (l) => {
      const t = l.trim();
      if (!t) rl.close();
      else parts.push(t);
    });
    rl.on("close", () => resolve(parts.join("")));
  });
}

async function main() {
  const args = parseArgs(process.argv);

  if (args.help) {
    console.log(`
用法：node tools\import-cookie.mjs [--cookie "..."] [--file cookie.txt]

不带参数时进入交互粘贴模式。
获取 Cookie 的方法见同目录 README.md，最省事的方式是：
  浏览器打开 https://ans.cauai.fun/workspace/benefits → 登录 →
  F12 → Network → 找任意一个 arcade 或 me/points 请求 →
  右键 Copy → Copy as cURL (bash) → 粘贴到这里。
`);
    return 0;
  }

  let raw = args.cookie || "";
  if (!raw && args.file) {
    if (!fs.existsSync(args.file)) {
      console.error(`✕ 文件不存在：${args.file}`);
      return 1;
    }
    raw = fs.readFileSync(args.file, "utf8");
  }

  if (!raw) {
    console.log(`
────────────────────────────────────────────────────────────
 获取 Cookie（约 30 秒，一次操作，通常可用 30 天）
────────────────────────────────────────────────────────────
 1. 浏览器打开并登录：https://ans.cauai.fun/workspace/benefits
 2. 按 F12 打开开发者工具，切到 Network（网络）标签
 3. 刷新页面，在列表里点任意一条 api 请求
    （例如 arcade 或 me/points，名字里带 api 的）
 4. 右键该请求 → Copy（复制）→ Copy as cURL (bash)
 5. 回到这里粘贴，然后按两次回车

 提示：也可以直接粘贴 "Cookie: xxx" 这一行，同样可以识别。
────────────────────────────────────────────────────────────
正在等待粘贴，粘贴后按两次回车：
`);
    raw = await readStdin();
  }

  const cookie = normalizeCookie(raw);
  if (!cookie) {
    console.error("✕ 没有解析出有效的 Cookie（格式应为 name=value; name2=value2）");
    return 1;
  }

  const names = cookieNames(cookie);
  console.log(`解析到 ${names.length} 个 Cookie：${names.join(", ")}`);

  const hasSession = names.some((n) => n.includes("session-token"));
  if (!hasSession) {
    console.log("! 警告：其中没有包含 session-token 的项，很可能没登录成功或复制错了请求。");
  }

  console.log("正在校验登录态…");
  let v;
  try {
    v = await validate(cookie);
  } catch (err) {
    console.error(`✕ 网络错误：${err?.message || err}`);
    return 1;
  }

  if (!v.ok) {
    console.error(`✕ 校验失败：${v.reason}`);
    console.error("  请确认复制的是登录状态下的请求，且浏览器里仍然处于登录状态。");
    return 2;
  }

  const id = v.user?.id || "";
  if (!id) {
    console.error("✕ 站点没有返回账号 ID，无法建档。");
    return 2;
  }

  const known = listAccounts();
  const existing = known.find((a) => a.id === id);

  saveAccount({
    id,
    email: v.user?.email || existing?.email || "",
    label: v.user?.name || existing?.label || v.user?.email || id,
    cookie,
    savedAt: new Date().toISOString(),
    enabled: true,
    needsLogin: false,
    lastError: "",
    pointsAtImport: extractPoints(v.points),
  });
  try {
    fs.rmSync(AUTH_MARKER, { force: true });
  } catch {
    /* ignore */
  }

  console.log("");
  console.log(existing ? "✓ 已更新账号登录态" : "✓ 已添加新账号");
  console.log(`  账号：${v.user.email || v.user.name || id}`);
  const p = extractPoints(v.points);
  if (p != null) console.log(`  当前积分：${p}`);
  console.log(`  当前共有 ${listAccounts().length} 个账号`);
  console.log("");
  console.log("下一步：执行 node checkin.mjs 试跑一次。");
  return 0;
}

main()
  .then((c) => {
    process.exitCode = c;
  })
  .catch((e) => {
    console.error("未预期错误：", e);
    process.exitCode = 1;
  });
