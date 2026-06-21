const { chromium } = require('playwright');
const fs = require('fs');
const { execSync } = require('child_process');

/**
 * .NET Debug Agent v0.2.0 — Full demo recording (34 tools / 10 inspectors)
 *
 * 7 sections using NATURAL LANGUAGE prompts (no explicit tool names).
 * The LLM must autonomously decide which tools to invoke.
 *
 * Usage:
 *   1. Start demo: cd demo && OPENAI_API_KEY=sk-... dotnet run --urls http://localhost:5199
 *   2. Run: node scripts/demo-record.js
 */

const BASE_URL = process.env.BASE_URL || 'http://localhost:5199';
const OUTPUT_DIR = './demo-recordings';
const VERSION = 'v02';

// ─── Helpers ──────────────────────────────────────────────────────────────

async function typeMessage(page, text, charDelay = 8) {
  const input = page.locator('#msg-input');
  await input.click();
  await input.pressSequentially(text, { delay: charDelay });
}

async function waitForAgentIdle(page, timeout = 120000) {
  // Wait for send button to be re-enabled
  try {
    await page.waitForFunction(() => {
      const btn = document.querySelector('#send-btn');
      return btn && !btn.disabled;
    }, { timeout });
  } catch {
    console.log('  Warning: Agent still busy, waiting more...');
    await page.waitForFunction(() => {
      const btn = document.querySelector('#send-btn');
      return btn && !btn.disabled;
    }, { timeout: 60000 }).catch(() => {
      console.log('  Warning: Force proceeding after extended wait');
    });
  }

  // Wait for DOM to stabilize (no new messages for 3s)
  let lastCount = 0;
  let stableTime = 0;
  let maxWait = 15000;
  const interval = 1000;
  while (stableTime < 3000 && maxWait > 0) {
    const count = await page.evaluate(() => document.querySelectorAll('.msg, .tool-event').length);
    if (count === lastCount) {
      stableTime += interval;
    } else {
      lastCount = count;
      stableTime = 0;
    }
    await page.waitForTimeout(interval);
    maxWait -= interval;
  }
  await page.waitForTimeout(1500);
}

async function sendAndWait(page, timeout = 120000) {
  await page.waitForSelector('#send-btn:not([disabled])', { timeout: 10000 }).catch(() => {});
  await page.locator('#send-btn').click();
  await waitForAgentIdle(page, timeout);
}

async function pause(page, ms = 3000) {
  await page.waitForTimeout(ms);
}

// ─── Section 1: .NET Runtime + Process ────────────────────────────────────
// Tools: get_memory_stats, trigger_gc, get_thread_pool_info, get_runtime_info,
//        get_process_info, get_environment_variables, get_disk_usage (7 tools)

async function section1_runtime(page) {
  console.log('  [1/7] .NET Runtime Deep Dive');
  await typeMessage(page, "My app feels sluggish. Can you check the overall runtime health — memory usage, GC stats, and how long the process has been running?");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me detailed thread pool information — how many worker threads are active, and are there any queued work items?");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What environment variables are set? Also check available disk space on the machine.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Try forcing a garbage collection — I want to see how much memory can be reclaimed.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 2: DI Container + Configuration ──────────────────────────────
// Tools: get_registered_services, get_service_count, get_service_detail,
//        resolve_service, get_configuration_sources, get_configuration_keys,
//        get_configuration_value (7 tools)

async function section2_di_config(page) {
  console.log('  [2/7] DI Container + Configuration');
  await typeMessage(page, "How many services are registered in the DI container? Break them down by lifetime — Singleton, Scoped, Transient.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me the OrderService registration details. Can you resolve it from the container and tell me its actual type?");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What configuration sources are loaded? Show me all the configuration keys and values — especially AppSettings.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What's the value of the connection string, and which configuration provider does it come from?");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 3: HTTP Endpoints + Request Tracking ─────────────────────────
// Tools: get_endpoints, get_endpoint_detail, get_recent_requests,
//        get_error_requests, get_request_stats (5 tools)

async function section3_http(page) {
  console.log('  [3/7] HTTP Endpoints + Request Tracking');
  await typeMessage(page, "What API endpoints does this application expose? List all the routes.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me details about the orders endpoint.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What HTTP requests have come in recently? Show me request statistics and any errors.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 4: Health Checks + Logging ───────────────────────────────────
// Tools: get_health_status, get_registered_health_checks, get_recent_logs,
//        search_logs, get_log_stats, get_log_levels (6 tools)

async function section4_health_logs(page) {
  console.log('  [4/7] Health Checks + Logging');
  await typeMessage(page, "Run all health checks and tell me the status of each component.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What health checks are registered? List them without executing.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me the recent application logs. What are the log level statistics?");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Search the logs for 'cache' — I want to see if there are any cache-related messages.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 5: EF Core + Database ────────────────────────────────────────
// Tools: get_db_contexts, get_db_context_info, get_db_migrations,
//        get_db_connection_stats (4 tools)

async function section5_efcore(page) {
  console.log('  [5/7] EF Core + Database');
  await typeMessage(page, "What DbContexts are registered in the application? Show me their types and connection info.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me detailed info about the OrderDbContext — what entity types it manages, provider, and connection details.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Check the database migrations — what's been applied and what's pending? Also show me connection statistics.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 6: Cache + Background Services ───────────────────────────────
// Tools: get_cache_keys, get_cache_stats, get_cache_value,
//        get_hosted_services, get_background_service_detail (5 tools)

async function section6_cache_bg(page) {
  console.log('  [6/7] Memory Cache + Background Services');
  await typeMessage(page, "What's in the memory cache? List all the cache keys and show me statistics.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "Show me the value of the 'all_orders' cache key.");
  await sendAndWait(page);
  await pause(page, 4000);

  await typeMessage(page, "What background services are running? Show me details about the OrderCleanupService.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Section 7: Comprehensive Debugging ───────────────────────────────────
// Cross-cutting scenario that exercises multiple inspectors together

async function section7_comprehensive(page) {
  console.log('  [7/7] Comprehensive Debugging Scenario');
  await typeMessage(page, "I'm debugging a performance issue. Give me a comprehensive overview: memory and GC status, thread pool, recent HTTP requests with errors, health check results, and recent logs — all in one summary.");
  await sendAndWait(page);
  await pause(page, 6000);

  await typeMessage(page, "Now check: how many DI services are registered, what's the cache size, and are there any pending EF Core migrations? Summarize the app's overall state.");
  await sendAndWait(page);
  await pause(page, 5000);
}

// ─── Main ─────────────────────────────────────────────────────────────────

(async () => {
  console.log(`
╔══════════════════════════════════════════════════════════════╗
║  .NET Debug Agent v0.2.0 — Demo Recording                   ║
║  34 tools / 10 inspectors                                   ║
╚══════════════════════════════════════════════════════════════╝
  `);

  if (!fs.existsSync(OUTPUT_DIR)) fs.mkdirSync(OUTPUT_DIR, { recursive: true });

  // Verify app is running
  console.log(`Checking app at ${BASE_URL}/agent ...`);
  try {
    const resp = await fetch(`${BASE_URL}/agent/api/tools`);
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const data = await resp.json();
    console.log(`  Found ${data.tools.length} tools registered`);
  } catch (e) {
    console.error(`ERROR: Demo app not running at ${BASE_URL}. Start it first:\n  cd demo && OPENAI_API_KEY=sk-... dotnet run --urls ${BASE_URL}`);
    process.exit(1);
  }

  const browser = await chromium.launch({ headless: false });
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    recordVideo: { dir: OUTPUT_DIR, size: { width: 1280, height: 800 } },
  });
  const page = await context.newPage();

  console.log(`Navigating to ${BASE_URL}/agent ...`);
  await page.goto(`${BASE_URL}/agent`);
  await pause(page, 2000);

  // Pre-generate some HTTP traffic for request tracking demos
  console.log('Generating HTTP traffic for demos...');
  const endpoints = [
    '/api/orders', '/api/orders/1', '/api/health',
    '/api/slow', '/api/error', '/api/orders',
    '/api/orders/1', '/api/health',
  ];
  for (const ep of endpoints) {
    try { await fetch(`${BASE_URL}${ep}`); } catch {}
  }

  // Pre-generate cache entries by hitting order endpoints
  try { await fetch(`${BASE_URL}/api/orders`); } catch {}
  try { await fetch(`${BASE_URL}/api/orders/1`); } catch {}

  await pause(page, 1000);

  const sections = [
    { name: '01-runtime', fn: section1_runtime },
    { name: '02-di-config', fn: section2_di_config },
    { name: '03-http', fn: section3_http },
    { name: '04-health-logs', fn: section4_health_logs },
    { name: '05-efcore', fn: section5_efcore },
    { name: '06-cache-bg', fn: section6_cache_bg },
    { name: '07-comprehensive', fn: section7_comprehensive },
  ];

  const startTime = Date.now();

  for (let i = 0; i < sections.length; i++) {
    const section = sections[i];
    const elapsed = ((Date.now() - startTime) / 60000).toFixed(1);
    console.log(`\n--- [${i + 1}/${sections.length}] ${section.name} (elapsed: ${elapsed} min) ---`);
    await section.fn(page);
    await page.screenshot({ path: `${OUTPUT_DIR}/${VERSION}-demo-${section.name}.png`, fullPage: true });
    console.log(`  Screenshot: ${VERSION}-demo-${section.name}.png`);
  }

  await pause(page, 3000);
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  await pause(page, 2000);

  const video = page.video();
  const videoPath = await video.path();
  console.log(`\n  Video path: ${videoPath}`);

  await context.close();
  await browser.close();

  // Rename and convert video
  console.log('\n--- Finalizing video ---');
  const finalWebm = `${OUTPUT_DIR}/${VERSION}-full-demo.webm`;
  const finalMp4 = `${OUTPUT_DIR}/${VERSION}-full-demo.mp4`;

  try { fs.unlinkSync(finalWebm); } catch {}
  try { fs.unlinkSync(finalMp4); } catch {}

  if (videoPath && fs.existsSync(videoPath)) {
    fs.copyFileSync(videoPath, finalWebm);
    const size = fs.statSync(finalWebm).size;
    console.log(`  Saved: ${VERSION}-full-demo.webm (${(size / 1024 / 1024).toFixed(1)} MB)`);
  }

  // Convert to mp4
  try {
    console.log('\n--- Converting to mp4 ---');
    if (fs.existsSync(finalWebm)) {
      execSync(`ffmpeg -y -i "${finalWebm}" -c:v libx264 -preset fast -crf 23 -c:a aac "${finalMp4}"`, { stdio: 'pipe' });
      const size = fs.statSync(finalMp4).size;
      console.log(`  Done: ${VERSION}-full-demo.mp4 (${(size / 1024 / 1024).toFixed(1)} MB)`);
    }
  } catch (e) {
    console.log('  (ffmpeg conversion failed, keeping .webm)');
  }

  const totalMin = ((Date.now() - startTime) / 60000).toFixed(1);
  console.log(`
======================================================
  Recording complete!
  Total time: ${totalMin} minutes
  Output: ${OUTPUT_DIR}/${VERSION}-full-demo.mp4
======================================================
  `);
})();
