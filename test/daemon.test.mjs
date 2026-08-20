import test from "node:test";
import assert from "node:assert/strict";
import { writeFileSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { cleanOutput, styleList, systemPromptFor, STYLES, DEFAULT_STYLE } from "../daemon/styles.mjs";
import { loadEnv } from "../daemon/env.mjs";

// ---------------------------------------------------------------- cleanOutput
//
// Every case here is either a preamble a model actually produced, or a piece of
// legitimate user content that an over-eager cleaner would destroy. The second
// group matters more: silently eating the first line of someone's prompt is a
// much worse bug than leaving a stray "Sure!" in place.

test("cleanOutput strips conversational preambles", () => {
  assert.equal(cleanOutput("Sure! \nWrite a haiku."), "Write a haiku.");
  assert.equal(cleanOutput("Here is the improved prompt:\nWrite a haiku."), "Write a haiku.");
  assert.equal(cleanOutput("Improved prompt:\nWrite a haiku."), "Write a haiku.");
  assert.equal(cleanOutput("Certainly.\n\nWrite a haiku."), "Write a haiku.");
});

test("cleanOutput unwraps a fence around the whole reply", () => {
  assert.equal(cleanOutput("```\nWrite a haiku.\n```"), "Write a haiku.");
  assert.equal(cleanOutput("```text\nWrite a haiku.\n```"), "Write a haiku.");
});

test("cleanOutput keeps a fence that is part of the prompt", () => {
  // The fence starts mid-text, so it is the user's code sample, not the model
  // formatting its answer. Unwrapping here would corrupt the prompt.
  const prompt = "Refactor this:\n\n```js\nconst x = 1;\n```\n\nKeep it pure.";
  assert.equal(cleanOutput(prompt), prompt);
});

test("cleanOutput strips a visible think block", () => {
  assert.equal(cleanOutput("<think>hmm, the user wants...</think>\nWrite a haiku."), "Write a haiku.");
});

test("cleanOutput strips a think block that precedes a fenced answer", () => {
  assert.equal(cleanOutput("<think>reasoning</think>\n```\nWrite a haiku.\n```"), "Write a haiku.");
});

test("cleanOutput unwraps whole-output quoting", () => {
  assert.equal(cleanOutput('"Write a haiku."'), "Write a haiku.");
});

test("cleanOutput leaves interior quotes alone", () => {
  // Balanced at the ends but genuinely quoted inside: stripping the outer pair
  // would change what the prompt asks for.
  const prompt = 'Explain the phrase "hello world" to a beginner.';
  assert.equal(cleanOutput(prompt), prompt);
});

test("cleanOutput does not eat a legitimate first line", () => {
  // "Here" begins a real sentence rather than a preamble; the pattern requires
  // a following newline, so this must survive intact.
  const prompt = "Here the user is a novice, so keep it simple.";
  assert.equal(cleanOutput(prompt), prompt);
});

test("cleanOutput handles empty and nullish input", () => {
  assert.equal(cleanOutput(""), "");
  assert.equal(cleanOutput(null), "");
  assert.equal(cleanOutput(undefined), "");
  assert.equal(cleanOutput("   \n  "), "");
});

// --------------------------------------------------------------------- styles

test("every style is well formed and exposed", () => {
  const list = styleList();
  assert.ok(list.length >= 5, "expected a useful number of styles");
  for (const { id, label, hint } of list) {
    assert.ok(STYLES[id], `${id} should exist in STYLES`);
    assert.ok(label.length > 0, `${id} needs a label`);
    assert.ok(hint.length > 0, `${id} needs a hint for the tray tooltip`);
  }
  assert.ok(STYLES[DEFAULT_STYLE], "the default style must exist");
});

test("every system prompt carries the do-not-answer framing", () => {
  // This is the one instruction the tool cannot work without: without it the
  // model answers the user's prompt instead of rewriting it.
  for (const id of Object.keys(STYLES)) {
    const prompt = systemPromptFor(id);
    assert.match(prompt, /<INPUT>/, `${id} must describe the input delimiter`);
    assert.match(prompt, /rewrite/i, `${id} must say it rewrites`);
    assert.ok(
      prompt.includes("You do not answer prompts"),
      `${id} must carry the do-not-answer rule`
    );
  }
});

test("an unknown style falls back to the default rather than throwing", () => {
  assert.equal(systemPromptFor("no-such-style"), systemPromptFor(DEFAULT_STYLE));
});

// ------------------------------------------------------------------------ env

test("loadEnv parses CRLF files without gluing \\r onto values", () => {
  // The bug this guards against is invisible: the key parses "successfully",
  // then every API call 401s because the header ends in a carriage return.
  const dir = mkdtempSync(path.join(tmpdir(), "refyn-env-"));
  const file = path.join(dir, ".env");
  writeFileSync(file, "PS_TEST_KEY=abc123\r\nPS_TEST_URL=https://example.com/v1\r\n");

  delete process.env.PS_TEST_KEY;
  delete process.env.PS_TEST_URL;
  loadEnv(file);

  assert.equal(process.env.PS_TEST_KEY, "abc123");
  assert.equal(process.env.PS_TEST_URL, "https://example.com/v1");
});

test("loadEnv strips surrounding quotes and ignores comments", () => {
  const dir = mkdtempSync(path.join(tmpdir(), "refyn-env-"));
  const file = path.join(dir, ".env");
  writeFileSync(file, ['# a comment', 'PS_TEST_QUOTED="quoted value"', "PS_TEST_PLAIN=plain"].join("\n"));

  delete process.env.PS_TEST_QUOTED;
  delete process.env.PS_TEST_PLAIN;
  loadEnv(file);

  assert.equal(process.env.PS_TEST_QUOTED, "quoted value");
  assert.equal(process.env.PS_TEST_PLAIN, "plain");
});

test("loadEnv never overrides a real environment variable", () => {
  const dir = mkdtempSync(path.join(tmpdir(), "refyn-env-"));
  const file = path.join(dir, ".env");
  writeFileSync(file, "PS_TEST_PRESET=from-file\n");

  process.env.PS_TEST_PRESET = "from-shell";
  loadEnv(file);
  assert.equal(process.env.PS_TEST_PRESET, "from-shell");
});

test("loadEnv on a missing file is a no-op, not a throw", () => {
  assert.equal(loadEnv(path.join(tmpdir(), "definitely-not-here-refyn", ".env")), false);
});
