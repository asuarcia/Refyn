#!/usr/bin/env node
/**
 * Refyn daemon.
 *
 * A localhost HTTP service holding the API key, the styles, and the history.
 * The tray host (host/RefynHost.cs) is the only expected client.
 *
 * Why a separate process at all, rather than doing the HTTP call from C#:
 * the styles in styles.mjs are the part that gets tuned constantly, and a tray
 * app that must be recompiled and restarted to change a system prompt would not
 * get tuned. The C# side stays dumb, stable, and compiled once.
 *
 * Binding: 127.0.0.1 only, never 0.0.0.0. This process will rewrite any text
 * posted to it using the user's API key, so it must not be reachable off-box.
 */

import http from "node:http";
import { loadEnv } from "./env.mjs";
import { rewrite, LlmError, MAX_INPUT_CHARS } from "./llm.mjs";
import { styleList, STYLES, DEFAULT_STYLE } from "./styles.mjs";
import { appendHistory, readHistory } from "./history.mjs";
import { readSettings, writeSettings } from "./settings.mjs";

loadEnv();

const PORT = Number(process.env.REFYN_PORT) || 8477;
const HOST = "127.0.0.1";
/** Requests larger than this are rejected before being read into memory. */
const MAX_BODY_BYTES = 64 * 1024;

const started = Date.now();
let rewrites = 0;

const server = http.createServer(async (req, res) => {
  try {
    // The host is a local process, but a browser on this machine could also
    // reach 127.0.0.1. Reject cross-origin requests outright rather than
    // relying on CORS preflight, which does not cover simple POSTs.
    if (req.headers.origin) {
      return send(res, 403, { error: "Refyn does not accept browser requests." });
    }

    const url = new URL(req.url, `http://${HOST}:${PORT}`);

    if (req.method === "GET" && url.pathname === "/health") {
      return send(res, 200, {
        ok: true,
        uptimeSec: Math.round((Date.now() - started) / 1000),
        rewrites,
        model: process.env.REFYN_MODEL || "deepseek-ai/deepseek-v4-flash-0731",
        keyConfigured: Boolean(process.env.NIM_API_KEY),
        pid: process.pid,
      });
    }

    if (req.method === "GET" && url.pathname === "/settings") {
      return send(res, 200, readSettings());
    }

    if (req.method === "POST" && url.pathname === "/settings") {
      const patch = await readJson(req);
      const changed = writeSettings(patch);
      if (changed.length) log("settings updated:", changed.join(", "));
      return send(res, 200, { ok: true, changed, ...readSettings() });
    }

    if (req.method === "GET" && url.pathname === "/styles") {
      return send(res, 200, { styles: styleList(), default: DEFAULT_STYLE });
    }

    if (req.method === "GET" && url.pathname === "/history") {
      const limit = Math.min(200, Number(url.searchParams.get("limit")) || 20);
      return send(res, 200, { entries: await readHistory(limit) });
    }

    if (req.method === "POST" && url.pathname === "/rewrite") {
      const body = await readJson(req);
      const text = typeof body.text === "string" ? body.text : "";
      const style = typeof body.style === "string" && STYLES[body.style] ? body.style : DEFAULT_STYLE;

      if (!text.trim()) {
        return send(res, 400, { error: "Nothing to rewrite — the selection was empty." });
      }

      const out = await rewrite(text, style);
      rewrites++;
      // Fire-and-forget: a history write must never delay the paste.
      appendHistory({ style, input: text, output: out.result, model: out.model, ms: out.ms })
        .catch((err) => log("history write failed:", err.message));

      log(`rewrite [${style}] ${text.length}ch -> ${out.result.length}ch in ${out.ms}ms`);
      return send(res, 200, { result: out.result, style, model: out.model, ms: out.ms });
    }

    return send(res, 404, { error: `No route for ${req.method} ${url.pathname}` });
  } catch (err) {
    if (err instanceof LlmError) {
      log("llm error:", err.message);
      return send(res, err.status === 429 ? 429 : 502, { error: err.message });
    }
    if (err?.code === "BODY_TOO_LARGE") {
      return send(res, 413, { error: `Selection too large (limit ${MAX_INPUT_CHARS} characters).` });
    }
    if (err?.code === "BAD_JSON") {
      return send(res, 400, { error: "Malformed JSON body." });
    }
    log("unhandled:", err?.stack || err);
    return send(res, 500, { error: String(err?.message || err) });
  }
});

function send(res, status, payload) {
  const json = JSON.stringify(payload);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(json),
    // The host parses JSON with a minimal hand-rolled reader; no surprises.
    "Cache-Control": "no-store",
  });
  res.end(json);
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        const err = new Error("body too large");
        err.code = "BODY_TOO_LARGE";
        req.destroy();
        reject(err);
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}"));
      } catch {
        const err = new Error("bad json");
        err.code = "BAD_JSON";
        reject(err);
      }
    });
    req.on("error", reject);
  });
}

function log(...args) {
  console.log(`[${new Date().toISOString()}]`, ...args);
}

server.on("error", (err) => {
  if (err.code === "EADDRINUSE") {
    console.error(`Port ${PORT} is already in use — Refyn may already be running.`);
    process.exit(2);
  }
  console.error(err);
  process.exit(1);
});

server.listen(PORT, HOST, () => {
  log(`Refyn daemon on http://${HOST}:${PORT}`);
  if (!process.env.NIM_API_KEY) {
    log("WARNING: NIM_API_KEY is not set. Rewrites will fail until it is in .env.");
  }
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    log("shutting down");
    server.close(() => process.exit(0));
    // Don't hang forever on a keep-alive connection from the host.
    setTimeout(() => process.exit(0), 1000).unref();
  });
}
