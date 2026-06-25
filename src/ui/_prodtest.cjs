const { chromium } = require("playwright");

(async () => {
  let browser;
  try {
    browser = await chromium.launch({ channel: "chrome", headless: true, args: ["--autoplay-policy=no-user-gesture-required", "--mute-audio"] });
    console.log("BROWSER: real Chrome (H.264/AAC/MP3 decode)");
  } catch (e) {
    browser = await chromium.launch({ headless: true });
    console.log("FALLBACK bundled chromium (NO H.264):", e.message.slice(0, 60));
  }
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const logs = [];
  page.on("console", (m) => logs.push(`[${m.type()}] ${m.text()}`.slice(0, 240)));
  page.on("pageerror", (e) => logs.push(`[PAGEERROR] ${(e.stack || e.message)}`.slice(0, 320)));
  let streamReqs = 0, streamErr = 0, progressPosts = 0;
  page.on("response", (r) => {
    const u = r.url();
    if (u.includes("stream.carpouzis.com")) { streamReqs++; if (r.status() >= 400) streamErr++; }
    if (u.includes("/API/Stream/Progress")) progressPosts++;
  });

  // auth as the test user (password -> amr=pwd)
  await page.goto("https://theater.carpouzis.com/", { waitUntil: "domcontentloaded" });
  const login = await page.evaluate(async () => {
    const r = await fetch("/API/Login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: "ClaudeStreamTest", password: "ClaudeStream!2026" }) });
    if (r.ok) localStorage.setItem("Username", "ClaudeStreamTest");
    return { status: r.status, ok: r.ok };
  });
  console.log("LOGIN:", JSON.stringify(login));

  // drive Gandhi
  await page.goto("https://theater.carpouzis.com/watch/691", { waitUntil: "domcontentloaded" });
  await page.waitForSelector("video", { timeout: 25000 }).catch(() => {});
  const samples = [];
  for (let i = 0; i < 7; i++) {
    await page.waitForTimeout(4000);
    samples.push(await page.evaluate(() => {
      const v = document.querySelector("video");
      if (!v) return { noVideo: true };
      return { t: +v.currentTime.toFixed(2), rs: v.readyState, ns: v.networkState, paused: v.paused,
        err: v.error ? `code${v.error.code}:${(v.error.message || "").slice(0, 60)}` : null,
        buf: v.buffered.length ? +v.buffered.end(v.buffered.length - 1).toFixed(1) : 0 };
    }));
  }
  console.log("VIDEO over time (4s apart):");
  samples.forEach((s, i) => console.log(`  +${(i + 1) * 4}s`, JSON.stringify(s)));
  const ui = await page.evaluate(() => ({
    total: document.querySelector(".vp-time-total")?.textContent || null,
    now: document.querySelector(".vp-time-now")?.textContent || null,
    errCard: document.querySelector(".watch-error-body")?.textContent || null,
    buffering: !!document.querySelector(".vp-bulbs"),
  }));
  console.log("UI:", JSON.stringify(ui));
  console.log("STREAM reqs:", streamReqs, "(errs", streamErr + ")", "ProgressPosts:", progressPosts);
  console.log("\n=== CONSOLE / HLS.js logs (last 45) ===");
  logs.slice(-45).forEach((l) => console.log("  " + l));
  await browser.close();
})();
