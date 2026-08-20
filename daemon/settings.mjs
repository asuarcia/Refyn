/**
 * Editable settings that live in .env rather than config.json.
 *
 * Split by owner, deliberately:
 *   config.json  — things the HOST needs before the daemon exists (hotkeys,
 *                  port, theme). Owned and written by the tray app.
 *   .env         — the model and the API key. Owned and written here, because
 *                  the daemon is the only process that should ever hold a key.
 *
 * The rewrite below is line-preserving: unknown keys, comments and ordering all
 * survive, because this file is hand-edited too and clobbering someone's
 * comments to change one value is hostile.
 */

import { readFileSync, writeFileSync, existsSync, renameSync, copyFileSync } from "node:fs";
import path from "node:path";
import { ROOT } from "./env.mjs";

const ENV_FILE = path.join(ROOT, ".env");

/** Keys this module is willing to write. Anything else is rejected. */
const WRITABLE = new Set(["NIM_API_KEY", "NIM_BASE_URL", "REFYN_MODEL"]);

const DEFAULT_MODEL = "deepseek-ai/deepseek-v4-flash-0731";

export function readSettings() {
  const key = process.env.NIM_API_KEY || "";
  return {
    model: process.env.REFYN_MODEL || DEFAULT_MODEL,
    baseUrl: process.env.NIM_BASE_URL || "https://integrate.api.nvidia.com/v1",
    keyConfigured: Boolean(key),
    // Never return the key itself — this endpoint is reachable by anything
    // running as the user. A masked tail is enough to tell two keys apart.
    keyMasked: key ? `${key.slice(0, 6)}…${key.slice(-4)}` : "",
    // The tray host has no idea where the repo lives or which Node started the
    // daemon, and it needs both to toggle the logon task on the user's behalf.
    appRoot: ROOT,
    node: process.execPath,
  };
}

/**
 * Apply a partial update to .env and to the live process.
 * Returns the fields that actually changed.
 */
export function writeSettings(patch) {
  const updates = {};

  if (typeof patch.model === "string" && patch.model.trim()) {
    updates.REFYN_MODEL = patch.model.trim();
  }
  if (typeof patch.baseUrl === "string" && patch.baseUrl.trim()) {
    const url = patch.baseUrl.trim();
    if (!/^https?:\/\//i.test(url)) throw new Error("Endpoint must start with http:// or https://");
    updates.NIM_BASE_URL = url;
  }
  // An empty apiKey means "leave it alone", not "clear it" — the settings UI
  // sends back a masked placeholder it never had the real value for.
  if (typeof patch.apiKey === "string" && patch.apiKey.trim() && !patch.apiKey.includes("…")) {
    updates.NIM_API_KEY = patch.apiKey.trim();
  }

  const changed = Object.keys(updates);
  if (!changed.length) return [];

  for (const name of changed) {
    if (!WRITABLE.has(name)) throw new Error(`Refusing to write ${name}`);
  }

  patchEnvFile(updates);
  for (const [name, value] of Object.entries(updates)) process.env[name] = value;

  return changed.map((name) => (name === "NIM_API_KEY" ? "apiKey" : name));
}

/**
 * Rewrite .env in place, replacing only the given keys.
 *
 * Writes through a temp file and a rename so a crash mid-write cannot leave a
 * truncated .env — which would lock the user out of their own API key with no
 * obvious cause.
 */
function patchEnvFile(updates) {
  const remaining = new Map(Object.entries(updates));
  let lines = [];

  if (existsSync(ENV_FILE)) {
    // Keep a one-shot backup before the first mutation of a hand-written file.
    if (!existsSync(ENV_FILE + ".bak")) copyFileSync(ENV_FILE, ENV_FILE + ".bak");
    lines = readFileSync(ENV_FILE, "utf8").split(/\r?\n/);
  }

  const out = lines.map((line) => {
    const m = /^(\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*=\s*)(.*)$/.exec(line);
    if (!m) return line;
    const name = m[2];
    if (!remaining.has(name)) return line;
    const value = remaining.get(name);
    remaining.delete(name);
    return `${m[1]}${name}=${value}`;
  });

  // Keys that were not already present get appended.
  for (const [name, value] of remaining) {
    out.push(`${name}=${value}`);
  }

  const temp = ENV_FILE + ".tmp";
  writeFileSync(temp, out.join("\n").replace(/\n*$/, "\n"), "utf8");
  renameSync(temp, ENV_FILE);
}
