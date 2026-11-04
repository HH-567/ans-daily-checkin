/**
 * 站点认证：账号密码登录 + Cookie 校验
 *
 * 站点是 NextAuth（Auth.js）的 credentials 方式，登录流程：
 *   GET  /api/auth/csrf                       → csrfToken + csrf/callback cookie
 *   POST /api/auth/callback/credentials        → 校验账号密码，成功则下发 session cookie
 *
 * Node 的 fetch 不自动保存 Cookie，这里手工维护一个极简 cookie jar。
 */
export const ORIGIN = "https://ans.cauai.fun";
export const TARGET = `${ORIGIN}/workspace/benefits`;

const UA =
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

function baseHeaders(extra = {}) {
  return {
    accept: "application/json, text/plain, */*",
    "accept-language": "zh-CN,zh;q=0.9",
    origin: ORIGIN,
    referer: TARGET,
    "user-agent": UA,
    "sec-fetch-site": "same-origin",
    "sec-fetch-mode": "cors",
    "sec-fetch-dest": "empty",
    ...extra,
  };
}

function collectCookies(res, jar) {
  let list = [];
  try {
    if (typeof res.headers.getSetCookie === "function") list = res.headers.getSetCookie();
  } catch {
    list = [];
  }
  if (!list.length) {
    const raw = res.headers.get("set-cookie");
    if (raw) list = raw.split(/,(?=[^;,]+=)/);
  }
  for (const item of list) {
    const pair = String(item).split(";")[0];
    const eq = pair.indexOf("=");
    if (eq <= 0) continue;
    jar.set(pair.slice(0, eq).trim(), pair.slice(eq + 1).trim());
  }
}

function cookieHeader(jar) {
  return [...jar.entries()].map(([k, v]) => `${k}=${v}`).join("; ");
}

async function withTimeout(fn, ms) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), ms);
  try {
    return await fn(ac.signal);
  } finally {
    clearTimeout(timer);
  }
}

/** 把粘贴进来的 Cookie（原始串 / curl 命令 / "Cookie: ..." 行）规整成标准格式 */
export function normalizeCookie(raw) {
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

export function cookieNames(cookie) {
  return String(cookie || "")
    .split(";")
    .map((c) => c.split("=")[0].trim())
    .filter(Boolean);
}

export function hasSessionCookie(cookie) {
  return cookieNames(cookie).some((n) => n.includes("session-token"));
}

/**
 * 用 Cookie 拉一次登录态与积分，确认这份凭证可用。
 * @returns {{ok:boolean, user?:object, points?:number|null, reason?:string}}
 */
export async function validateCookie(cookie, { timeoutMs = 15000 } = {}) {
  const ck = normalizeCookie(cookie);
  if (!ck) return { ok: false, reason: "Cookie 为空" };

  try {
    const sess = await withTimeout(
      (signal) =>
        fetch(`${ORIGIN}/api/auth/session`, {
          headers: baseHeaders({ cookie: ck }),
          signal,
        }),
      timeoutMs,
    );

    const sessJson = await sess.json().catch(() => null);
    if (!sessJson) {
      return { ok: false, reason: `会话接口返回 HTTP ${sess.status}（Cookie 可能已失效）` };
    }

    const user = sessJson.user ?? sessJson;
    if (!user?.id) return { ok: false, reason: "会话里没有账号信息" };

    let points = null;
    try {
      const pts = await withTimeout(
        (signal) => fetch(`${ORIGIN}/api/me/points`, { headers: baseHeaders({ cookie: ck }), signal }),
        timeoutMs,
      );
      const body = await pts.json().catch(() => null);
      if (pts.status === 200 && body) {
        for (const v of [body.points, body.balance, body.balancePoints, body.data?.points]) {
          if (typeof v === "number" && Number.isFinite(v)) {
            points = Math.round(v * 100) / 100;
            break;
          }
        }
      }
    } catch {
      /* 积分拿不到不影响登录态判定 */
    }

    return { ok: true, user, points, cookie: ck };
  } catch (err) {
    return { ok: false, reason: `网络错误：${err?.message || err}` };
  }
}

/**
 * 用邮箱 + 密码登录，成功后拿到 Cookie 与账号信息。
 * 密码只在内存里用，不落盘。
 *
 * @returns {{ok:true, cookie:string, user:object, points:number|null}
 *          | {ok:false, code:string, reason:string}}
 */
export async function loginWithPassword(email, password, { timeoutMs = 20000 } = {}) {
  const mail = String(email || "").trim();
  const pass = String(password || "");
  if (!mail) return { ok: false, code: "EMPTY_EMAIL", reason: "请填写邮箱" };
  if (!pass) return { ok: false, code: "EMPTY_PASSWORD", reason: "请填写密码" };

  const jar = new Map();

  try {
    // 1) 拿 csrfToken（同时种下 csrf / callback-url cookie）
    const csrfRes = await withTimeout(
      (signal) => fetch(`${ORIGIN}/api/auth/csrf`, { headers: baseHeaders(), signal }),
      timeoutMs,
    );
    collectCookies(csrfRes, jar);

    if (csrfRes.status !== 200) {
      return { ok: false, code: "CSRF_FAILED", reason: `获取校验令牌失败（HTTP ${csrfRes.status}）` };
    }
    const csrfJson = await csrfRes.json().catch(() => null);
    const csrfToken = csrfJson?.csrfToken;
    if (!csrfToken) return { ok: false, code: "CSRF_FAILED", reason: "响应里没有 csrfToken" };

    // 2) 提交账号密码
    const body = new URLSearchParams({
      csrfToken,
      email: mail,
      password: pass,
      callbackUrl: TARGET,
      redirectTo: TARGET,
      json: "true",
    });

    const res = await withTimeout(
      (signal) =>
        fetch(`${ORIGIN}/api/auth/callback/credentials`, {
          method: "POST",
          headers: baseHeaders({
            "content-type": "application/x-www-form-urlencoded",
            cookie: cookieHeader(jar),
          }),
          body,
          redirect: "manual",
          signal,
        }),
      timeoutMs,
    );

    collectCookies(res, jar);
    const cookie = cookieHeader(jar);

    // 3) 判断结果
    if (!hasSessionCookie(cookie)) {
      const location = res.headers.get("location") || "";
      const text = await res.text().catch(() => "");
      const haystack = `${location} ${text}`;

      if (/CredentialsSignin|callback\/credentials\?error/i.test(haystack)) {
        return { ok: false, code: "INVALID_CREDENTIALS", reason: "账号或密码不正确" };
      }
      if (res.status === 429) {
        return { ok: false, code: "RATE_LIMITED", reason: "尝试过于频繁，请稍后再试" };
      }
      if (res.status === 403) {
        return { ok: false, code: "FORBIDDEN", reason: "站点拒绝了登录请求（可能需要人机验证）" };
      }
      return {
        ok: false,
        code: "NO_SESSION",
        reason: `没有拿到登录态（HTTP ${res.status}），可能需要人机验证或账号被限制`,
      };
    }

    // 4) 用拿到的 Cookie 再校验一次，顺便取账号信息与积分
    const check = await validateCookie(cookie, { timeoutMs });
    if (!check.ok) {
      return { ok: false, code: "VERIFY_FAILED", reason: check.reason || "登录态校验失败" };
    }

    return { ok: true, cookie, user: check.user, points: check.points };
  } catch (err) {
    const aborted = err?.name === "AbortError";
    return {
      ok: false,
      code: aborted ? "TIMEOUT" : "NETWORK",
      reason: aborted ? "请求超时，请检查网络" : `网络错误：${err?.message || err}`,
    };
  }
}
