/**
 * .env loader.
 *
 * Deliberately hand-rolled rather than pulling in dotenv: this file is the one
 * dependency-free thing standing between the daemon and its API key, and a
 * three-line parser is easier to trust than a package.
 *
 * The `\r?` in the split is not cosmetic. A CRLF file parsed with /\n/ leaves a
 * trailing carriage return glued to every value, so the key becomes
 * "nvapi-...\r" and every request 401s with no useful error. That exact bug hid
 * in fifteen parsers in a sibling project for months. Windows editors write
 * CRLF by default, so this is the common case here, not the edge case.
 */
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

/** Load ROOT/.env into process.env. Real environment variables always win. */
export function loadEnv(file = path.join(ROOT, ".env")) {
  if (!existsSync(file)) return false;
  for (const line of readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (!m) continue;
    let value = m[2].trim();
    // Strip one layer of matching quotes, if present.
    if (value.length >= 2 && (value[0] === '"' || value[0] === "'") && value.at(-1) === value[0]) {
      value = value.slice(1, -1);
    }
    if (!(m[1] in process.env)) process.env[m[1]] = value;
  }
  return true;
}

export function requireEnv(name, hint) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set. ${hint ?? ""}`.trim());
  return value;
}
