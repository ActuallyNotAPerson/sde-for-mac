// Photographs the real phone form for docs/screenshots (90-phone-find and 91-phone-edit, light and dark).
// Run it while PhoneScreensHost serves made-up data; see README.md. Uses only Node's built-ins and Chrome.
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [folder, out] = process.argv.slice(2);
if (!folder || !out) {
  console.error("usage: node shoot.mjs <SDE_PHONE_SCREENS folder> <docs/screenshots folder>");
  process.exit(2);
}
const chrome = process.env.CHROME ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Wait for the host to say where it is.
let server;
for (let i = 0; i < 600 && !server; i++) {
  const file = join(folder, "server.json");
  if (existsSync(file)) { server = JSON.parse(readFileSync(file, "utf8")); } else { await sleep(100); }
}
if (!server) { console.error("No server.json: is PhoneScreensHost running?"); process.exit(1); }

const profile = mkdtempSync(join(tmpdir(), "sde-phone-"));
const port = 9300 + Math.floor(Math.random() * 500);
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, "--no-first-run", "--hide-scrollbars", "about:blank"], { stdio: "ignore" });

let version;
for (let i = 0; i < 100 && !version; i++) {
  try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await sleep(100); }
}
const ws = new WebSocket(version.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let nextId = 1;
const pending = new Map();
ws.addEventListener("message", (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); }
});
const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ id, method, params, sessionId }));
});

const { targetId } = await send("Target.createTarget", { url: "about:blank" });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
const page = (method, params) => send(method, params, sessionId);
const js = async (expression) => (await page("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result.value;
const waitFor = async (expression, what) => {
  for (let i = 0; i < 100; i++) { if (await js(expression)) { return; } await sleep(100); }
  const said = await js("(document.getElementById('message') || {}).textContent || ''");
  throw new Error("Timed out waiting for " + what + (said ? ". The page said: " + said : ""));
};
const shot = async (name) => {
  await sleep(400);
  const { data } = await page("Page.captureScreenshot", { format: "png" });
  writeFileSync(join(out, name + ".png"), Buffer.from(data, "base64"));
  console.log("wrote", name + ".png");
};

await page("Page.enable");
await page("Runtime.enable");
await page("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 2, mobile: true });
await page("Emulation.setTouchEmulationEnabled", { enabled: true });

// Pair with the one-time token, as the QR code would, giving the phone a name.
await page("Page.navigate", { url: server.baseAddress + "pair?t=" + server.token });
await waitFor("!!document.getElementById('pair-name') && !document.getElementById('pair').hidden", "the pairing screen");
await js("document.getElementById('pair-name').value = 'Inspector A'; document.getElementById('pair-form').requestSubmit(); true");
await waitFor("!document.getElementById('find').hidden", "the find screen");

for (const scheme of ["light", "dark"]) {
  const suffix = scheme === "dark" ? "-dark" : "";
  await page("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: scheme }] });
  await page("Page.navigate", { url: server.baseAddress });
  await waitFor("!document.getElementById('find').hidden", "the find screen");
  // My assignments for Team North.
  await waitFor("document.getElementById('team') && document.getElementById('team').options.length > 1", "the team list");
  await js("const t = document.getElementById('team'); t.value = t.options[1].value; t.dispatchEvent(new Event('change')); true");
  await waitFor("document.querySelectorAll('#assignments li').length > 0", "the assignments");
  await js("window.scrollTo(0, 0); true");
  await shot("90-phone-find" + suffix);
  // Open the first structure.
  await js("document.querySelector('#assignments li button').click(); true");
  await waitFor("!document.getElementById('edit').hidden && document.getElementById('edit-heading').textContent.length > 0", "the edit screen");
  await js("window.scrollTo(0, 0); true");
  await shot("91-phone-edit" + suffix);
}

ws.close();
const exited = new Promise((r) => proc.once("exit", r));
proc.kill();
await exited;
writeFileSync(join(folder, "done"), "done");
try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still be closing files; the folder is in the temp folder */ }
