import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const here = path.dirname(fileURLToPath(import.meta.url))
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const browser = await chromium.launch()
const page = await (await browser.newContext({ viewport: { width: 1100, height: 640 } })).newPage()
await page.addInitScript(() => localStorage.setItem('lots.dev-identity', JSON.stringify({ user: 'claude-test-nav', roles: 'operator' })))
await page.goto(`${base}/#/integrations/mcp`)
await page.getByRole('heading', { name: 'Integrations' }).waitFor()
console.log(await page.getByRole('tab').allInnerTexts())
console.log(await page.locator('nav a').allInnerTexts())
await page.screenshot({ path: path.resolve(here, 'out/integrations.png') })
await browser.close()
