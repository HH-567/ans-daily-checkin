#!/usr/bin/env node
/**
 * ANS 签到 · 用账号密码添加/刷新账号
 *
 * 直接走站点的 NextAuth credentials 登录接口，成功后把 Cookie 存进 accounts/。
 * 密码只在内存中使用，不写入磁盘、不出现在命令行参数里。
 *
 *   node login-password.mjs                 # 交互输入（密码不回显）
 *   node login-password.mjs --email a@b.com # 只预填邮箱，密码仍交互输入
 */
import readline from "node:readline";
import { loginWithPassword } from "./lib/ans-auth.mjs";
import { saveAccount, listAccounts } from "./lib/accounts.mjs";
import fs from "node:fs";
import path from "node:path";
import { ROOT } from "./lib/accounts.mjs";

const AUTH_MARKER = path.join(ROOT, "需要重新登录.txt");

function parseArgs(argv) {
  const out = { email: "", password: "", help: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--email" || a === "-e") out.email = argv[++i] || "";
    else if (a === "--password" || a === "-p") out.password = argv[++i] || "";
    else if (a === "--help" || a === "-h") out.help = true;
  }
  return out;
}

function ask(question) {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(question, (answer) => {
      rl.close();
      resolve(answer.trim());
    });
  });
}

/** 密码输入不回显 */
function askHidden(question) {
  return new Promise((resolve) => {
    const stdin = process.stdin;
    process.stdout.write(question);
    stdin.resume();
    stdin.setRawMode(true);
    stdin.setEncoding("utf8");
    let buf = "";

    const onData = (ch) => {
      if (ch === "\r" || ch === "\n") {
        stdin.setRawMode(false);
        stdin.pause();
        stdin.removeListener("data", onData);
        process.stdout.write("\n");
        resolve(buf);
        return;
      }
      if (ch === "\u0003") {
        // Ctrl+C
        stdin.setRawMode(false);
        process.stdout.write("\n");
        process.exit(130);
      }
      if (ch === "\u007f" || ch === "\b") {
        if (buf.length) {
          buf = buf.slice(0, -1);
          process.stdout.write("\b \b");
        }
        return;
      }
      if (ch >= " ") {
        buf += ch;
        process.stdout.write("*");
      }
    };
    stdin.on("data", onData);
  });
}

async function main() {
  const args = parseArgs(process.argv);
  if (args.help) {
    console.log(`
用法：node login-password.mjs [--email 邮箱]

  直接输入账号密码登录，成功后自动保存到 accounts/。
  密码输入时不回显，也不会存到磁盘。

提示：如果这个账号开了人机验证或第三方登录，请改用
      node grab-cookie.mjs（打开浏览器登录）或 node import-cookie.mjs（粘贴 Cookie）。
`);
    return 0;
  }

  const before = listAccounts();

  console.log("");
  console.log("────────────────────────────────────────────────────────────");
  console.log(" 用账号密码添加账号");
  console.log(" 密码只在本机内存中使用，不会保存到任何文件");
  console.log("────────────────────────────────────────────────────────────");
  console.log("");

  let email = args.email;
  if (!email) email = await ask("  邮箱：");
  if (!email) {
    console.error("✕ 邮箱不能为空");
    return 1;
  }

  const password = args.password || (await askHidden("  密码："));
  if (!password) {
    console.error("✕ 密码不能为空");
    return 1;
  }

  console.log("");
  console.log("  正在登录…");

  const result = await loginWithPassword(email, password);
  if (!result.ok) {
    console.error("");
    console.error(`✕ 登录失败：${result.reason}`);
    if (result.code === "INVALID_CREDENTIALS") {
      console.error("  请确认邮箱和密码是否正确。");
    } else if (result.code === "FORBIDDEN" || result.code === "NO_SESSION") {
      console.error("  该账号可能需要人机验证，请改用浏览器登录：");
      console.error("    node grab-cookie.mjs");
      console.error("  或粘贴 Cookie：");
      console.error("    node import-cookie.mjs");
    }
    return 2;
  }

  const existing = before.find((a) => a.id === result.user.id);
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

  try {
    fs.rmSync(AUTH_MARKER, { force: true });
  } catch {
    /* ignore */
  }

  console.log("");
  console.log(existing ? "✓ 已更新账号登录态" : "✓ 已添加新账号");
  console.log(`  账号：${result.user.email || email}`);
  console.log(`  昵称：${result.user.name || "—"}`);
  if (result.points != null) console.log(`  当前积分：${result.points}`);
  console.log(`  当前共有 ${listAccounts().length} 个账号`);
  console.log("");
  console.log("  下一步：双击 运行签到.bat，或在控制台点「全部执行」。");
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
