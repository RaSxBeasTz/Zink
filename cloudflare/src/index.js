const json = (value, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" }
});

const bytesToBase64 = bytes => btoa(String.fromCharCode(...bytes));
const base64ToBytes = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));
const hex = bytes => [...new Uint8Array(bytes)].map(x => x.toString(16).padStart(2, "0")).join("");
const sha256 = async text => hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));

async function passwordHash(password, salt) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
  return hex(await crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt, iterations: 100000 }, key, 256));
}

async function readBody(request) {
  try { return await request.json(); } catch { throw new Error("Invalid JSON body."); }
}

async function currentUser(request, env, queryToken = null) {
  const header = request.headers.get("authorization") || "";
  const token = queryToken || (header.toLowerCase().startsWith("bearer ") ? header.slice(7).trim() : "");
  if (!token) return null;
  const tokenHash = await sha256(token);
  return env.DB.prepare(`SELECT u.id, u.username, u.display_name, u.created_at
    FROM sessions s JOIN users u ON u.id=s.user_id
    WHERE s.token_hash=? AND s.expires_at > datetime('now')`).bind(tokenHash).first();
}

async function register(request, env) {
  const body = await readBody(request);
  const username = String(body.Username ?? body.username ?? "").trim();
  const password = String(body.Password ?? body.password ?? "");
  const displayName = String(body.DisplayName ?? body.displayName ?? username).trim() || username;
  if (!/^[A-Za-z0-9._-]{3,32}$/.test(username)) return json({ success: false, message: "Username must be 3-32 letters, numbers, dots, dashes or underscores." }, 400);
  if (password.length < 8) return json({ success: false, message: "Password must contain at least 8 characters." }, 400);
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const hash = await passwordHash(password, salt);
  try {
    await env.DB.prepare("INSERT INTO users(username,display_name,password_salt,password_hash) VALUES(?,?,?,?)")
      .bind(username, displayName, bytesToBase64(salt), hash).run();
    return json({ success: true, message: "Account created." });
  } catch (error) {
    if (String(error).toLowerCase().includes("unique")) return json({ success: false, message: "That username is already registered." }, 409);
    throw error;
  }
}

async function login(request, env) {
  const body = await readBody(request);
  const username = String(body.Username ?? body.username ?? "").trim();
  const password = String(body.Password ?? body.password ?? "");
  const user = await env.DB.prepare("SELECT * FROM users WHERE username=? COLLATE NOCASE").bind(username).first();
  if (!user) return json({ success: false, message: "Invalid username or password." }, 401);
  const actual = await passwordHash(password, base64ToBytes(user.password_salt));
  if (actual !== user.password_hash) return json({ success: false, message: "Invalid username or password." }, 401);
  const token = bytesToBase64(crypto.getRandomValues(new Uint8Array(32))).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
  await env.DB.prepare("INSERT INTO sessions(token_hash,user_id,expires_at) VALUES(?,?,datetime('now','+30 days'))")
    .bind(await sha256(token), user.id).run();
  return json({ success: true, userId: user.id, username: user.username, displayName: user.display_name, token });
}

async function authenticated(request, env, fn) {
  const user = await currentUser(request, env);
  return user ? fn(user) : json({ success: false, message: "Unauthorized." }, 401);
}

async function getOnlineUserIds(env) {
  const id = env.SIGNALING.idFromName("global");
  const response = await env.SIGNALING.get(id).fetch("https://signaling.internal/presence");
  if (!response.ok) throw new Error("Unable to read realtime presence.");
  const body = await response.json();
  return new Set((body.onlineUserIds || []).map(Number));
}

async function routeApi(request, env, url) {
  const path = url.pathname;
  if (request.method === "POST" && path === "/api/register") return register(request, env);
  if (request.method === "POST" && path === "/api/login") return login(request, env);
  if (request.method === "GET" && path === "/api/rtc-config") return authenticated(request, env, () => json({ success: true, iceServers: [{ urls: ["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"] }] }));
  if (request.method === "GET" && path === "/api/me") return authenticated(request, env, user => json({ id: user.id, username: user.username, displayName: user.display_name, signedUpAtUtc: user.created_at, socialLinks: [] }));
  if (request.method === "POST" && path === "/api/logout") return authenticated(request, env, async user => {
    const token = (request.headers.get("authorization") || "").slice(7).trim();
    await env.DB.prepare("DELETE FROM sessions WHERE token_hash=?").bind(await sha256(token)).run();
    return json({ success: true });
  });
  if (request.method === "GET" && path === "/api/users/search") return authenticated(request, env, async user => {
    const q = `%${(url.searchParams.get("q") || "").slice(0, 32)}%`;
    const result = await env.DB.prepare("SELECT id AS UserId, username AS Username, display_name AS DisplayName FROM users WHERE id<>? AND (username LIKE ? OR display_name LIKE ?) LIMIT 30").bind(user.id, q, q).all();
    return json(result.results);
  });
  if (request.method === "GET" && path === "/api/friends") return authenticated(request, env, async user => {
    const result = await env.DB.prepare(`SELECT u.id AS UserId,u.username AS Username,u.display_name AS DisplayName
      FROM friends f JOIN users u ON u.id=f.friend_id WHERE f.user_id=? ORDER BY u.username`).bind(user.id).all();
    const onlineUserIds = await getOnlineUserIds(env);
    return json(result.results.map(friend => {
      const isOnline = onlineUserIds.has(Number(friend.UserId));
      return { ...friend, IsOnline: isOnline, Presence: isOnline ? "Online" : "Offline" };
    }));
  });
  if (request.method === "POST" && path === "/api/friends/add") return authenticated(request, env, async user => {
    const body = await readBody(request); const name = String(body.Username ?? body.username ?? "").trim();
    const target = await env.DB.prepare("SELECT id FROM users WHERE username=? COLLATE NOCASE").bind(name).first();
    if (!target || target.id === user.id) return json({ success: false, message: "User not found." }, 404);
    await env.DB.batch([
      env.DB.prepare("INSERT OR IGNORE INTO friends(user_id,friend_id) VALUES(?,?)").bind(user.id, target.id),
      env.DB.prepare("INSERT OR IGNORE INTO friends(user_id,friend_id) VALUES(?,?)").bind(target.id, user.id)
    ]);
    return json({ success: true });
  });
  if (request.method === "GET" && path === "/api/friends/requests") return authenticated(request, env, () => json([]));
  return json({ success: false, message: "Not found." }, 404);
}

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      if (url.pathname === "/health") return json({ ok: true, service: "zink-free" });
      if (url.pathname === "/ws") {
        const user = await currentUser(request, env, url.searchParams.get("token"));
        if (!user) return new Response("Unauthorized", { status: 401 });
        const id = env.SIGNALING.idFromName("global");
        const headers = new Headers(request.headers);
        headers.set("x-zink-user-id", String(user.id));
        headers.set("x-zink-username", user.username);
        headers.set("x-zink-display-name", user.display_name);
        return env.SIGNALING.get(id).fetch(new Request(request, { headers }));
      }
      return routeApi(request, env, url);
    } catch (error) {
      console.error(error);
      return json({ success: false, message: "Server error." }, 500);
    }
  }
};

export class SignalingHub {
  constructor(state) { this.state = state; }

  async fetch(request) {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/presence") {
      const onlineUserIds = [...new Set(this.state.getWebSockets()
        .map(socket => Number(socket.deserializeAttachment()?.userId))
        .filter(userId => Number.isSafeInteger(userId) && userId > 0))];
      return json({ onlineUserIds });
    }

    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("WebSocket required", { status: 426 });
    const pair = new WebSocketPair();
    const client = pair[0], server = pair[1];
    const attachment = {
      userId: Number(request.headers.get("x-zink-user-id")),
      username: request.headers.get("x-zink-username") || "",
      displayName: request.headers.get("x-zink-display-name") || ""
    };
    this.state.acceptWebSocket(server);
    server.serializeAttachment(attachment);
    return new Response(null, { status: 101, webSocket: client });
  }

  webSocketMessage(socket, message) {
    const sender = socket.deserializeAttachment();
    if (typeof message === "string") {
      let envelope;
      try { envelope = JSON.parse(message); } catch { return; }
      const targetId = Number(envelope.TargetUserId ?? envelope.targetUserId ?? 0);
      envelope.FromUserId = sender.userId;
      envelope.FromUsername = sender.username;
      envelope.FromDisplayName = sender.displayName;
      this.sendTo(targetId, JSON.stringify(envelope));
      return;
    }
    const data = new Uint8Array(message);
    if (data.length < 13) return;
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const targetId = Number(view.getBigInt64(5, true));
    view.setBigInt64(5, BigInt(sender.userId), true);
    this.sendTo(targetId, data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength));
  }

  sendTo(userId, message) {
    for (const peer of this.state.getWebSockets()) {
      if (peer.deserializeAttachment()?.userId === userId) {
        try { peer.send(message); } catch { }
      }
    }
  }

  webSocketClose(socket, code, reason) { try { socket.close(code, reason); } catch { } }
  webSocketError(socket) { try { socket.close(1011, "WebSocket error"); } catch { } }
}
