/**
 * Inference client. OpenAI-compatible chat completions over plain fetch.
 *
 * Points at NVIDIA NIM by default because this is high-frequency, latency-bound
 * work sitting behind a keypress — it must be cheap and it must come back in a
 * couple of seconds. Any OpenAI-compatible endpoint works: set NIM_BASE_URL.
 */

import { systemPromptFor, cleanOutput, DEFAULT_STYLE, STYLES } from "./styles.mjs";

const DEFAULT_MODEL = "deepseek-ai/deepseek-v4-flash-0731";
const DEFAULT_BASE = "https://integrate.api.nvidia.com/v1";

/** Hard ceiling on input size. Beyond this the request is slow and the user
 *  almost certainly selected a whole document by accident. */
export const MAX_INPUT_CHARS = 12000;

export class LlmError extends Error {
  constructor(message, { status, retryable = false } = {}) {
    super(message);
    this.name = "LlmError";
    this.status = status;
    this.retryable = retryable;
  }
}

function config() {
  const apiKey = process.env.NIM_API_KEY;
  if (!apiKey) {
    throw new LlmError("No API key. Put NIM_API_KEY in Refyn's .env file.");
  }
  return {
    apiKey,
    baseUrl: (process.env.NIM_BASE_URL || DEFAULT_BASE).replace(/\/+$/, ""),
    model: process.env.REFYN_MODEL || DEFAULT_MODEL,
  };
}

/**
 * Rewrite one prompt.
 *
 * @param {string} text  the user's raw prompt
 * @param {string} styleId  a key of STYLES
 * @param {{signal?: AbortSignal, temperature?: number}} opts
 * @returns {Promise<{result: string, model: string, ms: number, tokens: object|null}>}
 */
export async function rewrite(text, styleId = DEFAULT_STYLE, opts = {}) {
  const input = (text ?? "").trim();
  if (!input) throw new LlmError("Nothing to rewrite — the selection was empty.");
  if (input.length > MAX_INPUT_CHARS) {
    throw new LlmError(
      `Selection is ${input.length} characters; the limit is ${MAX_INPUT_CHARS}. Select just the prompt.`
    );
  }
  const style = STYLES[styleId] ? styleId : DEFAULT_STYLE;
  const { apiKey, baseUrl, model } = config();

  // The delimiter is load-bearing: it is what lets the system prompt refer to
  // "the text between <INPUT> and </INPUT>" as data. Any such tags already in
  // the user's text are neutralised so they cannot close the payload early.
  const fenced = input.replace(/<\/?INPUT>/gi, (m) => m.replace(/</g, "‹"));

  const body = {
    model,
    messages: [
      { role: "system", content: systemPromptFor(style) },
      { role: "user", content: `<INPUT>\n${fenced}\n</INPUT>` },
    ],
    // Low but not zero: greedy decoding on a rewriting task tends to parrot the
    // input back unchanged.
    temperature: opts.temperature ?? 0.3,
    // Generous headroom over the input so a long prompt is never truncated
    // mid-sentence, which would be worse than failing outright.
    max_tokens: Math.min(4096, Math.max(512, Math.ceil(input.length / 2))),
  };

  const started = Date.now();
  const response = await fetchWithRetry(`${baseUrl}/chat/completions`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${apiKey}`,
    },
    body: JSON.stringify(body),
    signal: opts.signal,
  });

  const data = await response.json();
  const raw = data?.choices?.[0]?.message?.content ?? "";
  const result = cleanOutput(raw);

  if (!result) {
    // An empty completion is nearly always a refusal or a reasoning model that
    // spent its whole budget thinking. Say which, rather than pasting nothing.
    const reason = data?.choices?.[0]?.finish_reason;
    throw new LlmError(
      reason === "length"
        ? `${model} used its whole token budget without answering. Try a shorter selection or a non-reasoning model.`
        : `${model} returned nothing (finish_reason: ${reason ?? "unknown"}).`
    );
  }

  return {
    result,
    model,
    ms: Date.now() - started,
    tokens: data?.usage ?? null,
  };
}

/**
 * One retry on the failures that are actually transient. Rate limits and 5xx
 * get a second chance; a 401 does not, because retrying a bad key just doubles
 * the time the user waits for the same error.
 */
async function fetchWithRetry(url, init, attempt = 0) {
  let response;
  try {
    response = await fetch(url, init);
  } catch (cause) {
    if (cause?.name === "AbortError") throw cause;
    if (attempt === 0) {
      await sleep(400);
      return fetchWithRetry(url, init, attempt + 1);
    }
    throw new LlmError(`Cannot reach the inference endpoint: ${cause.message}`, { retryable: true });
  }

  if (response.ok) return response;

  const detail = await response.text().catch(() => "");
  const retryable = response.status === 429 || response.status >= 500;
  if (retryable && attempt === 0) {
    await sleep(response.status === 429 ? 1500 : 500);
    return fetchWithRetry(url, init, attempt + 1);
  }

  throw new LlmError(explainHttp(response.status, detail), {
    status: response.status,
    retryable,
  });
}

function explainHttp(status, detail) {
  const trimmed = detail.slice(0, 300);
  switch (status) {
    case 401:
    case 403:
      return "Inference endpoint rejected the API key (HTTP " + status + "). Check NIM_API_KEY in .env.";
    case 404:
      return `Model not found (HTTP 404). ${process.env.REFYN_MODEL || DEFAULT_MODEL} may have been retired — set REFYN_MODEL to a live one.`;
    case 410:
      return `Model reached end of life (HTTP 410). Set REFYN_MODEL to a current model.`;
    case 429:
      return "Rate limited (HTTP 429). Wait a moment and try again.";
    default:
      return `Inference failed (HTTP ${status})${trimmed ? ": " + trimmed : "."}`;
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
