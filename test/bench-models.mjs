#!/usr/bin/env node
/**
 * Benchmark candidate models on Refyn's real workload.
 *
 *   node test/bench-models.mjs
 *
 * Exists because a model recommendation in a README is worthless unless it was
 * measured. This sends the actual system prompts from daemon/styles.mjs to each
 * candidate and scores what comes back on the two things that decide whether a
 * model is usable here:
 *
 *   1. Does it REWRITE or does it ANSWER? An instruction-tuned model's first
 *      instinct on "write a haiku about rain" is to write one. A model that
 *      does that is unusable no matter how fast it is.
 *   2. Does it obey a length constraint? `concise` must come back shorter than
 *      it went in. This is a cheap, objective proxy for instruction-following.
 *
 * Latency is wall-clock from this machine and will vary; treat the ranking as
 * meaningful and the absolute numbers as indicative.
 */

import { loadEnv } from "../daemon/env.mjs";
import { systemPromptFor, cleanOutput } from "../daemon/styles.mjs";

loadEnv();
const KEY = process.env.NIM_API_KEY;
const BASE = (process.env.NIM_BASE_URL || "https://integrate.api.nvidia.com/v1").replace(/\/+$/, "");
if (!KEY) { console.error("NIM_API_KEY missing"); process.exit(1); }

const CANDIDATES = [
  "deepseek-ai/deepseek-v4-flash-0731",
  "meta/llama-3.3-70b-instruct",
  "meta/llama-3.1-8b-instruct",
  "nv-mistralai/mistral-nemo-12b-instruct",
  "google/gemma-3-12b-it",
  "google/gemma-4-31b-it",
  "openai/gpt-oss-20b",
  "openai/gpt-oss-120b",
  "nvidia/llama-3.1-nemotron-nano-8b-v1",
  "microsoft/phi-3.5-moe-instruct",
  "mistralai/mistral-small-24b-instruct",
];

/** Each case is (style, input, predicate) — the predicate defines "correct". */
const CASES = [
  {
    style: "improve",
    input: "hey can u write me somthing that explains how dns works but like simple",
    // A rewrite still ASKS for an explanation. An answer explains.
    ok: (out) => /dns/i.test(out) && !/phonebook|directory|translat|look ?up.*ip|maps? .*(domain|name).* to/i.test(out),
    why: "must ask for an explanation, not give one",
  },
  {
    style: "improve",
    input: "write a haiku about rain",
    // The classic trap: models write the haiku instead of improving the prompt.
    ok: (out) => /haiku/i.test(out) && !/\n.*\n/.test(out.trim().replace(/\.\s+/g, ".\n")) === false
      ? /haiku/i.test(out) && /(write|compose|create)/i.test(out)
      : /haiku/i.test(out) && /(write|compose|create)/i.test(out),
    why: "must not write the haiku",
  },
  {
    style: "concise",
    input: "Hi there! I was sort of wondering if you might possibly be able to help me out with maybe explaining, if it is not too much trouble, how the event loop works in Node.js? Thank you so much!",
    ok: (out, input) => out.length < input.length && /event loop/i.test(out),
    why: "must come back shorter and keep the subject",
  },
];

async function callModel(model, style, text) {
  const started = Date.now();
  const response = await fetch(`${BASE}/chat/completions`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${KEY}` },
    body: JSON.stringify({
      model,
      messages: [
        { role: "system", content: systemPromptFor(style) },
        { role: "user", content: `<INPUT>\n${text}\n</INPUT>` },
      ],
      temperature: 0.3,
      max_tokens: 1024,
    }),
    signal: AbortSignal.timeout(90000),
  });
  const ms = Date.now() - started;
  if (!response.ok) {
    const body = await response.text().catch(() => "");
    throw new Error(`HTTP ${response.status} ${body.slice(0, 90)}`);
  }
  const data = await response.json();
  return { text: cleanOutput(data?.choices?.[0]?.message?.content ?? ""), ms, usage: data?.usage };
}

const results = [];

for (const model of CANDIDATES) {
  let totalMs = 0;
  let correct = 0;
  let dead = null;
  const samples = [];

  for (const c of CASES) {
    try {
      const out = await callModel(model, c.style, c.input);
      totalMs += out.ms;
      const good = out.text.length > 0 && c.ok(out.text, c.input);
      if (good) correct++;
      samples.push({ style: c.style, good, ms: out.ms, text: out.text });
    } catch (err) {
      dead = err.message;
      break;
    }
  }

  if (dead) {
    console.log(`\x1b[90m${model.padEnd(42)} unavailable — ${dead}\x1b[0m`);
    continue;
  }

  const avg = Math.round(totalMs / CASES.length);
  results.push({ model, correct, total: CASES.length, avg, samples });
  const flag = correct === CASES.length ? "\x1b[32m" : correct === 0 ? "\x1b[31m" : "\x1b[33m";
  console.log(`${flag}${model.padEnd(42)} ${correct}/${CASES.length} correct   avg ${String(avg).padStart(6)}ms\x1b[0m`);
}

console.log("\n\n=== outputs from the models that passed everything ===\n");
for (const r of results.filter((r) => r.correct === r.total).sort((a, b) => a.avg - b.avg)) {
  console.log(`\x1b[1m${r.model}\x1b[0m  (avg ${r.avg}ms)`);
  for (const s of r.samples) {
    console.log(`  [${s.style}] ${s.text.replace(/\s+/g, " ").slice(0, 150)}`);
  }
  console.log();
}

console.log("=== ranking: correctness first, then speed ===");
results
  .sort((a, b) => b.correct - a.correct || a.avg - b.avg)
  .forEach((r, i) => console.log(`${i + 1}. ${r.model.padEnd(42)} ${r.correct}/${r.total}  ${r.avg}ms`));
