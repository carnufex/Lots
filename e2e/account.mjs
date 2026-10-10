// Account page (#155) in a real Chromium: theme switches without a reload and follows the user to a fresh browser, the data export
// downloads, and deleting asks for the typed confirmation first. Usage: node account.mjs (dev auth with header identities on :8088).
import { chromium } from 'playwright'

const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const user = `claude-test-account-${Date.now()}`
const browser = await chromium.launch()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

async function fresh() {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: 'dark', acceptDownloads: true })
  await context.addInitScript((u) => {
    localStorage.setItem('lots.dev-identity', JSON.stringify({ user: u, roles: 'operator' }))
    localStorage.setItem('lots.ui-language', 'en')
  }, user)
  return context.newPage()
}

const page = await fresh()
await page.goto(`${base}/#/account/settings`)
await page.locator('.settings').getByLabel('Theme').waitFor()
ok((await page.evaluate(() => document.documentElement.dataset.theme)) === 'dark', 'system theme follows the browser (dark)')
await page.evaluate(() => { window.__sameDocument = true })
await page.locator('.settings').getByLabel('Theme').selectOption('light')
await page.getByText('Saved.').waitFor()
ok((await page.evaluate(() => [document.documentElement.dataset.theme, window.__sameDocument])).join() === 'light,true', 'theme switches to light without a reload')
ok(await page.locator('img.logo-light').isVisible(), 'the light wordmark is shown on light')

const other = await fresh() // another device: nothing in its storage
await other.goto(`${base}/#/runs`)
await other.waitForFunction(() => document.documentElement.dataset.theme === 'light', null, { timeout: 5000 }).then(() => ok(true, 'the theme follows the user to another browser'), () => ok(false, 'the theme follows the user to another browser'))

await page.locator('summary', { hasText: user }).click()
ok(await page.getByRole('menuitem', { name: 'Account' }).isVisible(), 'the user menu links to the account page')

await page.goto(`${base}/#/account/data`)
const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download everything (zip)' }).click()])
ok(download.suggestedFilename().endsWith('.zip'), `export downloads a zip (${download.suggestedFilename()})`)

let prompt = ''
page.once('dialog', (d) => { prompt = d.message(); void d.dismiss() })
await page.getByRole('button', { name: 'Delete my data' }).click()
await page.waitForTimeout(300)
ok(prompt.includes('delete my data'), 'delete asks for the typed confirmation')
ok(!(await page.getByText('Deleted:').isVisible()), 'cancelling deletes nothing')
page.once('dialog', (d) => void d.accept('delete my data'))
await page.getByRole('button', { name: 'Delete my data' }).click()
ok(await page.getByText('Deleted:').waitFor({ timeout: 5000 }).then(() => true, () => false), 'confirming deletes the data')

await page.goto(`${base}/#/usage`)
await page.getByRole('heading', { name: 'Usage' }).waitFor()
ok((await page.getByRole('button', { name: 'Download everything (zip)' }).count()) === 0, 'Usage no longer carries Your data')

await browser.close()
process.exit(failed ? 1 : 0)
