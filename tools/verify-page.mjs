// Verify the wallpaper really lives behind the desktop icons and is rendering.
// Needs DevTools in the app's WebView2: set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222 before starting LiveWall.
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function page() {
  for (let i = 0; i < 40; i++) {
    try {
      const list = await (await fetch('http://127.0.0.1:9222/json/list')).json();
      const p = list.find((t) => t.type === 'page');
      if (p) return p;
    } catch {}
    await sleep(500);
  }
  throw new Error('no devtools page found');
}

const p = await page();
console.log('page url:', p.url);

const ws = new WebSocket(p.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

const send = (id, method, params) =>
  new Promise((res) => {
    const h = (e) => {
      const m = JSON.parse(e.data);
      if (m.id === id) { ws.removeEventListener('message', h); res(m.result); }
    };
    ws.addEventListener('message', h);
    ws.send(JSON.stringify({ id, method, params }));
  });

const evalJs = async (expr) => {
  const r = await send(1, 'Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  return r?.result?.value;
};

console.log('title       :', await evalJs('document.title || "(none)"'));
console.log('video src   :', await evalJs('(document.querySelector("video")||{}).src || "(no video element)"'));
console.log('readyState  :', await evalJs('(document.querySelector("video")||{}).readyState ?? "n/a"'), '(4 = enough data)');
console.log('currentTime :', Math.round((await evalJs('(document.querySelector("video")||{}).currentTime ?? 0')) * 100) / 100);
console.log('size        :', await evalJs('(v=>v?`${v.videoWidth}x${v.videoHeight}`:"n/a")(document.querySelector("video"))'));
console.log('paused      :', await evalJs('(document.querySelector("video")||{}).paused ?? "n/a"'));
await sleep(1500);
console.log('currentTime2:', Math.round((await evalJs('(document.querySelector("video")||{}).currentTime ?? 0')) * 100) / 100, '(moved => actually playing)');
console.log('loop        :', await evalJs('(document.querySelector("video")||{}).loop ?? "n/a"'));
ws.close();
