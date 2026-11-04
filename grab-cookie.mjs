#!/usr/bin/env node
/**
 * ANS 签到 · 一键添加/刷新账号登录态（无需开发者工具）
 *
 * 流程：启动一个干净的浏览器窗口 → 你正常登录 → 脚本自动检测到登录成功
 *       → 读取该会话的 Cookie → 按账号 ID 存入 accounts/ → 自动关闭窗口并清理临时配置。
 *
 * 每次都用全新的临时浏览器配置，所以可以连续添加多个不同账号，不会串号。
 *
 *   node grab-cookie.mjs                  # 添加账号（登录哪个就存哪个）
 *   node grab-cookie.mjs --timeout 600    # 自定义等待登录秒数（默认 900）
 *   node grab-cookie.mjs --headless       # 无界面（仅调试用）
 */
import fs from "node:fs";
import path from "node:path";
import { ROOT, saveAccount, listAccounts, migrateLegacy } from "./lib/accounts.mjs";

const ORIGIN = "https://ans.cauai.fun";
const TARGET = `${ORIGIN}/workspace/benefits`;
const PROFILE_ROOT = path.join(ROOT, "browser-profile");
const AUTH_MARKER = path.join(ROOT, "需要重新登录.txt");

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function parseArgs(argv) {
  const out = { headless: false, timeout: 900 };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--headless") out.headless = true;
    else if (a === "--timeout") out.timeout = Math.max(30, Number(argv[++i]) || 900);
    else if (a === "--help" || a === "-h") out.help = true;
  }
  return out;
}

function cookieHeaderFrom(cookies) {
  return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
}

function cookieNames(cookie) {
  return cookie
    .split(";")
    .map((c) => c.split("=")[0].trim())
    .filter(Boolean);
}

async function validateWith(cookie) {
  try {
    const res = await fetch(`${ORIGIN}/api/auth/session`, {
      headers: {
        accept: "application/json",
        cookie,
        origin: ORIGIN,
        referer: TARGET,
        "user-agent":
          "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
      },
    });
    const json = await res.json().catch(() => null);
    if (!json) return { ok: false, reason: `会话接口返回 HTTP ${res.status}` };
    return { ok: true, user: json.user ?? json };
  } catch (err) {
    return { ok: false, reason: err?.message || String(err) };
  }
}

async function main() {
  const args = parseArgs(process.argv);
  if (args.help) {
    console.log(`
用法：node grab-cookie.mjs [--timeout 秒] [--headless]

  （无参数）        弹出干净浏览器窗口，登录成功后自动保存为账号并关闭
  --timeout 900     最长等待秒数，默认 900（15 分钟）
  --headless        不显示窗口（仅调试用）

可以重复执行来添加多个不同账号；登录哪个就保存哪个。
`);
    return 0;
  }

  let chromium;
  try {
    ({ chromium } = await import("playwright-core"));
  } catch {
    console.error("✕ 缺少 playwright-core，请先在项目目录执行：npm install");
    return 1;
  }

  migrateLegacy();
  const before = listAccounts();

  // 每次都用全新临时配置，保证不会带出上一个账号的登录态
  const profileDir = path.join(PROFILE_ROOT, `tmp-${Date.now()}`);
  fs.mkdirSync(profileDir, { recursive: true });

  console.log("");
  console.log("────────────────────────────────────────────────────────────");
  console.log(" 正在启动浏览器，稍等…");
  console.log(" 窗口出现后，请登录要添加的 ANS 账号");
  console.log(" 登录成功后脚本会自动保存并关闭窗口，无需其他操作");
  if (before.length) {
    console.log(` 已有账号：${before.map((a) => a.email || a.id).join("、")}`);
  }
  console.log("────────────────────────────────────────────────────────────");
  console.log("");

  let ctx;
  try {
    ctx = await chromium.launchPersistentContext(profileDir, {
      channel: "msedge",
      headless: args.headless,
      viewport: args.headless ? { width: 1280, height: 900 } : null,
      args: ["--no-first-run", "--no-default-browser-check", "--start-maximized"],
      locale: "zh-CN",
      timezoneId: "Asia/Shanghai",
    });
  } catch (err) {
    console.error(`✕ 无法启动浏览器：${err?.message || err}`);
    console.error("");
    console.error("  请确认本机安装了 Microsoft Edge。若确实没有，可用备用方案：");
    console.error("    1) 浏览器登录 " + TARGET);
    console.error("    2) F12 → Network → 复制任意 api 请求为 cURL");
    console.error("    3) 执行 node import-cookie.mjs 并粘贴");
    cleanupProfile(profileDir);
    return 1;
  }

  let closed = false;
  ctx.on("close", () => {
    closed = true;
  });

  const page = ctx.pages()[0] || (await ctx.newPage());

  try {
    await page.goto(TARGET, { waitUntil: "domcontentloaded", timeout: 45000 }).catch(() => {});
  } catch {
    /* 导航失败也继续，靠后续轮询判断 */
  }

  const deadline = Date.now() + args.timeout * 1000;
  let loggedIn = null;
  let waited = 0;

  while (!closed && Date.now() < deadline) {
    try {
      loggedIn = await page.evaluate(async () => {
        try {
          const r = await fetch("/api/auth/session", { cache: "no-store" });
          if (!r.ok) return null;
          return await r.json();
        } catch {
          return null;
        }
      });
    } catch {
      // 页面可能正在导航，忽略
    }

    if (loggedIn) break;

    await sleep(1500);
    waited += 1.5;
    if (waited % 10 < 1.5) {
      const left = Math.max(0, Math.round((deadline - Date.now()) / 1000));
      console.log(`  等待登录中…（已等待 ${Math.round(waited)} 秒，剩余 ${left} 秒）`);
    }
  }

  if (closed) {
    console.log("");
    console.log("  浏览器窗口被关闭，尝试保存已捕获的登录态…");
  }

  if (!loggedIn) {
    console.error("");
    console.error("✕ 未检测到登录状态，没有保存。");
    console.error("  请在浏览器窗口里完成登录后重试；或改用 node import-cookie.mjs 手动粘贴。");
    await ctx.close().catch(() => {});
    cleanupProfile(profileDir);
    return 2;
  }

  const cookies = await ctx.cookies(ORIGIN);
  const cookie = cookieHeaderFrom(cookies);
  const user = loggedIn.user ?? loggedIn;

  if (!cookie) {
    console.error("✕ 没有读到任何 Cookie，未保存。");
    await ctx.close().catch(() => {});
    cleanupProfile(profileDir);
    return 2;
  }

  const check = await validateWith(cookie);
  if (!check.ok) {
    console.error(`✕ Cookie 校验未通过：${check.reason}`);
    await ctx.close().catch(() => {});
    cleanupProfile(profileDir);
    return 2;
  }

  const id = user?.id || "";
  if (!id) {
    console.error("✕ 站点没有返回账号 ID，无法建档。");
    await ctx.close().catch(() => {});
    cleanupProfile(profileDir);
    return 2;
  }

  const existing = before.find((a) => a.id === id);
  saveAccount({
    id,
    email: user?.email || existing?.email || "",
    label: user?.name || existing?.label || user?.email || id,
    cookie,
    savedAt: new Date().toISOString(),
    enabled: true,
    needsLogin: false,
    lastError: "",
  });

  try {
    fs.rmSync(AUTH_MARKER, { force: true });
  } catch {
    /* ignore */
  }

  await ctx.close().catch(() => {});
  cleanupProfile(profileDir);

  const total = listAccounts().length;
  console.log("");
  console.log(existing ? "✓ 已更新账号登录态" : "✓ 已添加新账号");
  console.log(`  账号：${user?.email || user?.name || id}`);
  console.log(`  昵称：${user?.name || "—"}`);
  console.log(`  Cookie 项数：${cookieNames(cookie).length}`);
  console.log(`  当前共有 ${total} 个账号`);
  console.log("");
  console.log("  下一步：双击 运行签到.bat，或在控制台点「立即执行」。");
  return 0;
}

function cleanupProfile(dir) {
  try {
    fs.rmSync(dir, { recursive: true, force: true });
  } catch {
    /* 占用中的文件删不掉，忽略即可 */
  }
}

main()
  .then((c) => {
    process.exitCode = c;
  })
  .catch((e) => {
    console.error("未预期错误：", e);
    process.exitCode = 1;
  });
