import assert from 'node:assert/strict';
import { startPolling } from '../web/src/polling.ts';

let now = 0, sequence = 0;
const timers = new Map();
const originals = { setTimeout, clearTimeout, now: Date.now, random: Math.random };
globalThis.document = Object.assign(new EventTarget(), { visibilityState: 'visible' });
globalThis.window = new EventTarget();
Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { onLine: true } });
Date.now = () => now;
Math.random = () => 0;
globalThis.setTimeout = (callback, delay) => { const id = ++sequence; timers.set(id, { callback, at: now + delay }); return id; };
globalThis.clearTimeout = id => timers.delete(id);
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
async function tick(ms) {
  const end = now + ms;
  while (true) {
    const next = [...timers].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
    if (!next) break;
    now = next[1].at; timers.delete(next[0]); void next[1].callback(); await flush();
  }
  now = end; await flush();
}
try {
  let calls = 0, release;
  let stop = startPolling(() => { calls++; return new Promise(resolve => { release = resolve; }); }, 1000);
  await tick(0); assert.equal(calls, 1);
  await tick(60_000); assert.equal(calls, 1, 'overlapping slow request');
  release(); await flush(); await tick(999); assert.equal(calls, 1);
  await tick(1); assert.equal(calls, 2);
  stop(); release(); await flush(); assert.equal(timers.size, 0, 'unmounted poll scheduled another request');

  calls = 0; document.visibilityState = 'hidden';
  stop = startPolling(async () => { calls++; }, 1000);
  await tick(60_000); assert.equal(calls, 0, 'hidden page polling');
  document.visibilityState = 'visible'; document.dispatchEvent(new Event('visibilitychange'));
  await tick(0); assert.equal(calls, 1);
  navigator.onLine = false; window.dispatchEvent(new Event('offline'));
  await tick(60_000); assert.equal(calls, 1, 'offline polling');
  navigator.onLine = true; window.dispatchEvent(new Event('online'));
  await tick(0); assert.equal(calls, 2); stop();

  calls = 0;
  stop = startPolling(async () => { calls++; throw { retryAfterMs: 10_000 }; }, 1000);
  await tick(0); assert.equal(calls, 1);
  document.visibilityState = 'hidden'; document.dispatchEvent(new Event('visibilitychange'));
  await tick(5000); document.visibilityState = 'visible'; document.dispatchEvent(new Event('visibilitychange'));
  await tick(4999); assert.equal(calls, 1, 'visibility change discarded Retry-After');
  await tick(1); assert.equal(calls, 2); stop();
  assert.equal(timers.size, 0);
  console.log('PASS: sequential polling; no overlap; hidden/offline pause and resume; Retry-After survives resume; cleanup during in-flight requests');
} finally {
  globalThis.setTimeout = originals.setTimeout; globalThis.clearTimeout = originals.clearTimeout;
  Date.now = originals.now; Math.random = originals.random;
}
