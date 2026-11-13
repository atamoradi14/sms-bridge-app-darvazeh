// One request at a time; pause hidden/offline tabs and retain server cooldowns on resume.
export function startPolling(task: () => Promise<unknown>, interval: number, initialDelay = 0) {
  let stopped = false, busy = false, failures = 0, nextAt = Date.now() + initialDelay;
  let timer: ReturnType<typeof setTimeout>;
  const available = () => document.visibilityState !== "hidden" && navigator.onLine !== false;
  const arm = () => {
    clearTimeout(timer);
    if (!stopped && !busy && available()) timer = setTimeout(run, Math.max(0, nextAt - Date.now()));
  };
  async function run() {
    if (stopped || busy || !available()) return;
    busy = true;
    let delay = interval;
    try { await task(); failures = 0; }
    catch (error) {
      failures++;
      const hint = Number((error as { retryAfterMs?: number })?.retryAfterMs) || 0;
      delay = Math.max(hint, Math.min(300_000, interval * 2 ** Math.min(failures, 6))) + Math.random() * 1000;
    } finally {
      busy = false; nextAt = Date.now() + delay; arm();
    }
  }
  document.addEventListener("visibilitychange", arm);
  window.addEventListener("online", arm);
  window.addEventListener("offline", arm);
  arm();
  return () => {
    stopped = true; clearTimeout(timer);
    document.removeEventListener("visibilitychange", arm);
    window.removeEventListener("online", arm);
    window.removeEventListener("offline", arm);
  };
}
