// Vocabulary on the website (Voice > Vocabulary, #154): add a word, it persists, and it fixes the spelling in a real dictation.
// Uses a real recording as the fake microphone (fixtures/sv-intro.wav is git-ignored: it is the owner's voice).
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const here = path.dirname(fileURLToPath(import.meta.url))
const wav = path.resolve(here, 'fixtures/sv-intro.wav')
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const user = `e2e-${Date.now()}` // a fresh dev identity so the test never touches real vocabulary
const browser = await chromium.launch({ args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${wav}`] })
const ctx = await browser.newContext({ permissions: ['microphone'] })
await ctx.addInitScript((u) => localStorage.setItem('lots.dev-identity', JSON.stringify({ user: u, roles: 'operator' })), user)
const page = await ctx.newPage()
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) process.exitCode = 1 }

// Start from an empty list (dev auth maps every browser to the same user unless identity headers are enabled).
await fetch(`${base}/voice/vocabulary`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ words: [] }) })
await page.goto(`${base}/#/chat`)
ok((await page.locator('summary', { hasText: 'Vocabulary' }).count()) === 0, 'the composer has no second vocabulary editor')
await page.getByRole('link', { name: 'Add it to your vocabulary' }).click()
await page.getByLabel('Add words').waitFor()
ok(page.url().endsWith('#/voice/vocabulary'), 'the composer links to Voice > Vocabulary')
await page.getByLabel('Add words').fill('Christopher, Lots')
const saved = page.waitForResponse((r) => r.url().includes('/voice/vocabulary') && r.request().method() === 'PUT')
await page.getByLabel('Add words').press('Enter')
await page.getByRole('listitem').filter({ hasText: 'Christopher' }).waitFor()
await saved
ok(true, 'words added as chips (comma separated input)')

await page.reload()
await page.getByLabel('Add words').waitFor()
ok(await page.getByRole('listitem').filter({ hasText: 'Christopher' }).count() === 1, 'vocabulary persisted on the server across a reload')

// The tab's test box shows the corrected transcript without starting anything.
await page.getByLabel('Speech language').selectOption({ label: 'Svenska' })
await page.getByRole('button', { name: 'Dictate', exact: true }).click()
await page.waitForTimeout(6500)
await page.getByRole('button', { name: 'Stop', exact: true }).click()
await page.locator('.dictated').waitFor({ timeout: 30000 })
ok(/Christopher/.test(await page.locator('.dictated').innerText()), 'test dictation shows the corrected transcript')

// Dictation in the chat composer uses the words exactly as before.
await page.goto(`${base}/#/chat`)
await page.getByLabel('Speech language').selectOption({ label: 'Svenska' })
await page.getByRole('button', { name: 'Dictate', exact: true }).click()
await page.waitForTimeout(6500)
await page.getByRole('button', { name: 'Stop', exact: true }).click()
await page.getByRole('button', { name: 'Dictate', exact: true }).waitFor({ timeout: 30000 })
const text = await page.locator('textarea').inputValue()
console.log('     dictated:', JSON.stringify(text))
ok(/Christopher/.test(text) && !/Christoffer/.test(text), 'the name is spelled as in the vocabulary')

await page.goto(`${base}/#/voice/vocabulary`)
const removed = page.waitForResponse((r) => r.url().includes('/voice/vocabulary') && r.request().method() === 'PUT')
await page.getByRole('button', { name: 'Remove Lots' }).click()
await removed
await page.reload()
await page.getByLabel('Add words').waitFor()
ok(await page.getByRole('listitem').filter({ hasText: /^Lots/ }).count() === 0, 'a removed word stays removed')
await browser.close()
