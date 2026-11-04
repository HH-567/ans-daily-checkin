#!/usr/bin/env node
/**
 * ANS 签到 · 添加账号（命令行菜单）
 *
 *   1) 账号密码  → 直接调站点登录接口换取 Cookie（推荐，最快）
 *   2) 粘贴 Cookie → 从浏览器复制（适合已经登录过、或密码不方便输入的场合）
 *   3) 浏览器登录 → 弹一个干净窗口手动登录（适合开了人机验证的账号）
 */
import readline from "node:readline";
import path from "node:path";
import { spawn } from "node:child_process";
import { ROOT, listAccounts } from "./lib/accounts.mjs";

function ask(question) {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    rl.question(question, (ans) => {
      rl.close();
      resolve(ans.trim());
    });
  });
}

function runScript(file) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [path.join(ROOT, file)], {
      cwd: ROOT,
      stdio: "inherit",
    });
    child.on("exit", (code) => resolve(code ?? 0));
    child.on("error", () => resolve(1));
  });
}

async function main() {
  const existing = listAccounts();

  console.log("");
  console.log("════════════════════════════════════════════════════════════");
  console.log(" 添加 ANS 账号");
  console.log("════════════════════════════════════════════════════════════");
  if (existing.length) {
    console.log(` 当前已有 ${existing.length} 个账号：${existing.map((a) => a.email || a.id).join("、")}`);
    console.log("");
  }
  console.log(" 请选择登录方式：");
  console.log("   1) 账号密码      —— 输入邮箱和密码，自动登录（推荐）");
  console.log("   2) 粘贴 Cookie   —— 从浏览器复制，不需要密码");
  console.log("   3) 浏览器登录    —— 弹出干净窗口手动登录（需人机验证时用）");
  console.log("");

  const choice = await ask(" 输入 1 / 2 / 3 后回车：");

  let file;
  if (choice === "1") file = "login-password.mjs";
  else if (choice === "2") file = "import-cookie.mjs";
  else if (choice === "3") file = "grab-cookie.mjs";
  else {
    console.error("✕ 无效选择，请输入 1、2 或 3");
    return 1;
  }

  console.log("");
  const code = await runScript(file);
  return code;
}

main()
  .then((c) => {
    process.exitCode = c;
  })
  .catch((e) => {
    console.error("未预期错误：", e);
    process.exitCode = 1;
  });
