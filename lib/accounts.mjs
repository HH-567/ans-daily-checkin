/**
 * 账号存储：一个账号一个文件，放在 accounts/ 下。
 *
 * 文件名用站点返回的用户 ID，避免中文邮箱/昵称带来的路径问题。
 * 同时负责把旧版的单账号 session.json 自动迁移过来。
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
export const ACCOUNTS_DIR = path.join(ROOT, "accounts");
export const LEGACY_SESSION = path.join(ROOT, "session.json");
export const AUTH_MARKER = path.join(ROOT, "需要重新登录.txt");

function ensureDir() {
  fs.mkdirSync(ACCOUNTS_DIR, { recursive: true });
}

function safeId(raw) {
  const s = String(raw || "").trim();
  if (!s) return "";
  return s.replace(/[^A-Za-z0-9_-]/g, "");
}

export function accountFile(id) {
  return path.join(ACCOUNTS_DIR, `${safeId(id)}.json`);
}

function readJson(file, fallback = null) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return fallback;
  }
}

export function listAccounts() {
  ensureDir();
  const out = [];
  let names = [];
  try {
    names = fs.readdirSync(ACCOUNTS_DIR).filter((n) => n.endsWith(".json"));
  } catch {
    return out;
  }
  for (const name of names) {
    const rec = readJson(path.join(ACCOUNTS_DIR, name), null);
    if (!rec || !rec.cookie) continue;
    out.push({
      id: rec.id || name.replace(/\.json$/, ""),
      email: rec.email || "",
      label: rec.label || rec.email || rec.id,
      cookie: rec.cookie,
      savedAt: rec.savedAt || null,
      enabled: rec.enabled !== false,
      needsLogin: !!rec.needsLogin,
      lastError: rec.lastError || "",
      lastCheckedAt: rec.lastCheckedAt || null,
    });
  }
  out.sort((a, b) => String(a.savedAt || "").localeCompare(String(b.savedAt || "")));
  return out;
}

export function getAccount(id) {
  const sid = safeId(id);
  if (!sid) return null;
  return listAccounts().find((a) => a.id === sid) || null;
}

/** 支持按 id、邮箱、昵称模糊匹配一个账号 */
export function resolveAccount(needle) {
  const q = String(needle || "").trim().toLowerCase();
  if (!q) return null;
  const list = listAccounts();
  return (
    list.find((a) => a.id.toLowerCase() === q) ||
    list.find((a) => a.email.toLowerCase() === q) ||
    list.find((a) => (a.label || "").toLowerCase() === q) ||
    list.find((a) => a.email.toLowerCase().includes(q) || (a.label || "").toLowerCase().includes(q)) ||
    null
  );
}

export function saveAccount(rec) {
  ensureDir();
  const id = safeId(rec.id);
  if (!id) throw new Error("账号缺少 id");
  const file = accountFile(id);
  const prev = readJson(file, {}) || {};
  const merged = {
    ...prev,
    ...rec,
    id,
    updatedAt: new Date().toISOString(),
    note: "此文件等同于登录凭证，请勿分享或提交到公开仓库",
  };
  fs.writeFileSync(file, JSON.stringify(merged, null, 2), "utf8");
  return merged;
}

export function patchAccount(id, patch) {
  const file = accountFile(id);
  const prev = readJson(file, null);
  if (!prev) return null;
  const merged = { ...prev, ...patch, id: safeId(id) };
  fs.writeFileSync(file, JSON.stringify(merged, null, 2), "utf8");
  return merged;
}

export function deleteAccount(id) {
  const file = accountFile(id);
  try {
    fs.rmSync(file, { force: true });
    return true;
  } catch {
    return false;
  }
}

/** 旧版单账号 session.json → accounts/<id>.json */
export function migrateLegacy() {
  const legacy = readJson(LEGACY_SESSION, null);
  if (!legacy || !legacy.cookie) return null;
  const id = safeId(legacy.user?.id || legacy.id || "");
  if (!id) return null;
  if (fs.existsSync(accountFile(id))) return null;
  const rec = saveAccount({
    id,
    email: legacy.user?.email || "",
    label: legacy.user?.name || legacy.user?.email || id,
    cookie: legacy.cookie,
    savedAt: legacy.savedAt || new Date().toISOString(),
    enabled: true,
    migratedFrom: "session.json",
  });
  try {
    fs.renameSync(LEGACY_SESSION, path.join(ROOT, "session.json.migrated"));
  } catch {
    /* 迁移标记失败不影响账号可用 */
  }
  return rec;
}

/** 把需要重新登录的账号写到提示文件里 */
export function writeAuthMarker(accounts, detailText = "") {
  try {
    if (!accounts.length) {
      fs.rmSync(AUTH_MARKER, { force: true });
      return;
    }
    const lines = [
      "ANS 每日签到：以下账号登录态已失效",
      "",
      `时间：${new Date().toLocaleString("zh-CN", { timeZone: "Asia/Shanghai" })}`,
      ...accounts.map((a) => `  - ${a.email || a.label || a.id}`),
      "",
      detailText ? `原因：${detailText}` : "",
      "处理方法：打开控制台点「重新登录」并选择该账号，或用命令行 node grab-cookie.mjs 重新登录。",
      "全部账号恢复后本文件会自动删除。",
    ].filter(Boolean);
    fs.writeFileSync(AUTH_MARKER, lines.join("\r\n"), "utf8");
  } catch {
    /* ignore */
  }
}
