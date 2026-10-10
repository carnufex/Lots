// Permission-aware navigation (#157) in a real Chromium: the menu shows only what the roles allow, and a direct link to a page
// without access explains itself instead of failing. Usage: node navigation.mjs (dev auth with header identities on :8088).
import { chromium } from 'playwright'

const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const browser = await chromium.launch()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

async function as(roles) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  await context.addInitScript((r) => {
    localStorage.setItem('lots.dev-identity', JSON.stringify({ user: 'claude-test-nav', roles: r }))
    localStorage.setItem('lots.ui-language', 'en')
  }, roles)
  return context.newPage()
}

const operator = await as('operator')
await operator.goto(`${base}/#/chat`)
await operator.getByRole('link', { name: 'Chat' }).first().waitFor()
await operator.waitForTimeout(800)
const links = await operator.locator('#mainnav a').allInnerTexts()
for (const hidden of ['Policy', 'Identity', 'Contexts', 'Audit', 'Feedback', 'Insights', 'Models'])
  ok(!links.some((l) => l.trim() === hidden), `operator does not see ${hidden}`)
for (const shown of ['Chat', 'History', 'Knowledge']) ok(links.some((l) => l.trim() === shown), `operator sees ${shown}`)
await operator.goto(`${base}/#/policy`)
ok(await operator.getByText('You do not have access to this page').waitFor({ timeout: 5000 }).then(() => true, () => false), 'a direct link to Policy explains the missing access')

const admin = await as('admin')
await admin.goto(`${base}/#/chat`)
await admin.getByRole('link', { name: 'Policy' }).waitFor({ timeout: 10000 })
ok(true, 'admin sees Policy')

await browser.close()
process.exit(failed ? 1 : 0)
