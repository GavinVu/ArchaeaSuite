// Drives the launcher's WebView2 over the Chrome DevTools Protocol.
//   node cdp.mjs shot out.png
//   node cdp.mjs eval "document.title"
//   node cdp.mjs click "text or css selector"
//   node cdp.mjs size 1200 780
import { writeFile } from "node:fs/promises";

const port = process.env.CDP_PORT ?? 9222;
const [cmd, ...args] = process.argv.slice(2);
const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const page = targets.find((t) => t.type === "page");
if (!page) throw new Error("no page target");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r, j) => ((ws.onopen = r), (ws.onerror = j)));
let seq = 0;
const pending = new Map();
ws.onmessage = (m) => {
  const msg = JSON.parse(m.data);
  if (msg.id && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  }
};
const send = (method, params = {}) =>
  new Promise((resolve) => {
    const id = ++seq;
    pending.set(id, resolve);
    ws.send(JSON.stringify({ id, method, params }));
  });
const evaluate = async (expression) => {
  const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
  return r.result.result.value;
};

if (cmd === "shot") {
  const r = await send("Page.captureScreenshot", { format: "png" });
  await writeFile(args[0], Buffer.from(r.result.data, "base64"));
  console.log("saved", args[0]);
} else if (cmd === "eval") {
  console.log(JSON.stringify(await evaluate(args[0]), null, 1));
} else if (cmd === "click") {
  // Click the first visible button whose text/aria-label matches, else a CSS selector.
  const needle = JSON.stringify(args[0]);
  const nth = Number(args[1] ?? 0);
  console.log(
    await evaluate(`(() => {
      const needle = ${needle};
      const els = [...document.querySelectorAll('button, a, [role=radio], [role=switch]')].filter(e => e.offsetParent !== null);
      let hits = els.filter(e => e.textContent.trim() === needle || e.getAttribute('aria-label') === needle);
      if (!hits.length) hits = els.filter(e => e.textContent.trim().startsWith(needle));
      if (!hits.length) { try { hits = [...document.querySelectorAll(needle)]; } catch {} }
      const el = hits[${nth}];
      if (!el) return 'NOT FOUND: ' + needle + ' among [' + els.map(e => e.textContent.trim() || e.getAttribute('aria-label')).join(' | ') + ']';
      if (el.disabled) return 'DISABLED: ' + needle;
      el.click();
      return 'clicked ' + (el.textContent.trim() || el.getAttribute('aria-label'));
    })()`)
  );
} else if (cmd === "size") {
  await send("Emulation.setDeviceMetricsOverride", { width: +args[0], height: +args[1], deviceScaleFactor: 1, mobile: false });
  console.log("ok");
}
ws.close();
