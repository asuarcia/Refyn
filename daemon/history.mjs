/**
 * Rewrite history, as JSON Lines.
 *
 * Kept because the single most common thing a user wants after a rewrite is the
 * previous one back — either the original they just overwrote, or a rewrite they
 * pasted into the wrong window. Append-only lines survive a kill -9 mid-write
 * with at most one corrupt trailing line, which the reader tolerates.
 *
 * This file contains whatever the user selected, so it lives next to the .env
 * in the repo root and is gitignored alongside it.
 */

import { appendFile, readFile, stat, rename } from "node:fs/promises";
import path from "node:path";
import { ROOT } from "./env.mjs";

const FILE = process.env.PROMPTSMITH_HISTORY || path.join(ROOT, "history.jsonl");
/** Rotate at 5MB. One rolled file is kept; older history is not interesting. */
const MAX_BYTES = 5 * 1024 * 1024;

export async function appendHistory(entry) {
  await rotateIfLarge();
  const line = JSON.stringify({ at: new Date().toISOString(), ...entry }) + "\n";
  await appendFile(FILE, line, "utf8");
}

/** Most recent `limit` entries, newest first. */
export async function readHistory(limit = 20) {
  let raw;
  try {
    raw = await readFile(FILE, "utf8");
  } catch (err) {
    if (err.code === "ENOENT") return [];
    throw err;
  }
  const out = [];
  const lines = raw.split("\n");
  for (let i = lines.length - 1; i >= 0 && out.length < limit; i--) {
    const line = lines[i].trim();
    if (!line) continue;
    try {
      out.push(JSON.parse(line));
    } catch {
      // A torn final line from an interrupted write. Skip it.
    }
  }
  return out;
}

async function rotateIfLarge() {
  try {
    const { size } = await stat(FILE);
    if (size > MAX_BYTES) await rename(FILE, FILE + ".1");
  } catch (err) {
    if (err.code !== "ENOENT") throw err;
  }
}

export const HISTORY_FILE = FILE;
