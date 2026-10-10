// Screenshots of the conversation history for a test identity (never the real user's). Usage: node history-shot.mjs <conversation id>
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const here = path.dirname(fileURLToPath(import.meta.url))
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const browser = await chromium.launch()
const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
await page.addInitScript(() => localStorage.setItem('lots.dev-identity', JSON.stringify({ user: 'claude-test-history', roles: 'operator' })))
await page.goto(`${base}/#/history`)
await page.getByRole('heading', { name: 'Conversation history' }).waitFor()
await page.getByRole('link', { name: /Vilka containrar/ }).first().waitFor()
await page.screenshot({ path: path.resolve(here, 'out/history-list.png') })
await page.goto(`${base}/#/history/${process.argv[2]}`)
await page.getByText('Where the time went').waitFor()
await page.screenshot({ path: path.resolve(here, 'out/history-detail.png'), fullPage: true })
await browser.close()
