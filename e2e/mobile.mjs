// Phone layout and accessibility checks (#113) in a real Chromium at 375x812: no sideways scrolling, the navigation is reachable,
// every control has an accessible name, keyboard focus is visible. Screenshots go to out/mobile-*.png for review.
// Usage: node mobile.mjs        (needs the web UI served on :8088, dev auth with header identities)
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'

const here = path.dirname(fileURLToPath(import.meta.url))
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const out = path.resolve(here, 'out')
fs.mkdirSync(out, { recursive: true })

const browser = await chromium.launch()
const context = await browser.newContext({ viewport: { width: 375, height: 812 }, hasTouch: true, deviceScaleFactor: 2 })
await context.addInitScript(() => localStorage.setItem('lots.dev-identity', JSON.stringify({ user: 'claude-test-mobile', roles: 'operator,admin' })))
const page = await context.newPage()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

for (const route of ['chat', 'runs', 'approvals', 'history', 'usage', 'voice', 'knowledge', 'integrations', 'audit', 'profiles', 'insights', 'feedback']) {
  await page.goto(`${base}/#/${route}`)
  await page.waitForTimeout(900)
  const m = await page.evaluate(() => ({
    scroll: document.documentElement.scrollWidth,
    width: innerWidth,
    offenders: [...document.querySelectorAll('main *')]
      .filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > innerWidth + 1 && getComputedStyle(e).position !== 'fixed' })
      .filter((e) => {
        // Content clipped by a scrolling ancestor inside the viewport is fine (wide tables scroll in place).
        for (let a = e.parentElement; a && a.tagName !== 'MAIN'; a = a.parentElement) {
          const o = getComputedStyle(a).overflowX
          if ((o === 'auto' || o === 'scroll' || o === 'hidden') && a.getBoundingClientRect().right <= innerWidth + 1) return false
        }
        return true
      })
      .slice(0, 4).map((e) => `${e.tagName.toLowerCase()}.${String(e.className).split(' ')[0]}`),
    unnamed: [...document.querySelectorAll('button, a[href], input, select, textarea')]
      .filter((e) => e.offsetParent !== null)
      .filter((e) => !((e.getAttribute('aria-label') || e.textContent || e.getAttribute('title') || e.getAttribute('placeholder') || (e.labels && e.labels[0]?.textContent) || '').trim()))
      .slice(0, 4).map((e) => e.outerHTML.slice(0, 80)),
  }))
  ok(m.scroll <= m.width + 1 && m.offenders.length === 0, `${route}: no sideways scroll (${m.scroll}px of ${m.width}px) ${m.offenders.join(' ')}`)
  ok(m.unnamed.length === 0, `${route}: every visible control has a name ${m.unnamed.join(' | ')}`)
  await page.screenshot({ path: path.join(out, `mobile-${route}.png`), fullPage: false })
}

// The navigation must be reachable on a phone.
await page.goto(`${base}/#/chat`)
await page.waitForTimeout(500)
const menu = page.getByRole('button', { name: /menu|meny/i })
if (await menu.count()) {
  await menu.first().click()
  await page.waitForTimeout(300)
  await page.screenshot({ path: path.join(out, 'mobile-menu.png') })
}
ok(await page.getByRole('link', { name: /approvals|godkännanden/i }).first().isVisible(), 'navigation to Approvals is reachable')

// Keyboard: the first Tab lands on the skip link, and focus is visible.
await page.goto(`${base}/#/runs`)
await page.reload() // a hash-only goto keeps the focus from the menu test
await page.waitForTimeout(500)
await page.keyboard.press('Tab')
const focus = await page.evaluate(() => {
  const e = document.activeElement
  const s = e ? getComputedStyle(e) : null
  return { text: e?.textContent?.trim() ?? '', outline: s ? `${s.outlineStyle} ${s.outlineWidth}` : '' }
})
ok(/skip|hoppa/i.test(focus.text), `first Tab goes to the skip link (${focus.text})`)
ok(!focus.outline.startsWith('none'), `focus is visible (${focus.outline})`)

await browser.close()
process.exit(failed ? 1 : 0)
