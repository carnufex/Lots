// Browser end-to-end check of dictation with a fake microphone (Chromium plays a wav file as the mic).
// Usage: node dictation.mjs [fixtures/hallo-sv.wav] [Auto|Svenska|English]
// Needs the stack running on :8088 with voice enabled.
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const wav = path.resolve(here, process.argv[2] ?? 'fixtures/hallo-sv.wav')
const language = process.argv[3] ?? 'Auto'
const base = process.env.LOTS_URL ?? 'http://localhost:8088'

const browser = await chromium.launch({
  args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${wav}`],
})
const page = await (await browser.newContext({ permissions: ['microphone'] })).newPage()
const problems = []
page.on('console', (m) => m.type() === 'error' && problems.push('console: ' + m.text()))
page.on('pageerror', (e) => problems.push('pageerror: ' + e.message))
page.on('response', (r) => r.url().includes('/voice/') && console.log(`  ${r.request().method()} ${new URL(r.url()).pathname} -> ${r.status()}`))

await page.goto(`${base}/#/chat`)
await page.getByLabel('Speech language').selectOption({ label: language })
const prompt = page.locator('textarea')

async function dictate(n) {
  const button = page.getByRole('button', { name: /Dictate|Stop|Transcribing/ })
  console.log(`dictation ${n}: button says "${(await button.innerText()).trim()}", disabled=${await button.isDisabled()}`)
  await button.click({ timeout: 5000 })
  await page.waitForTimeout(3500)
  await page.getByRole('button', { name: 'Stop', exact: true }).click({ timeout: 5000 })
  const before = await prompt.inputValue()
  await page.waitForFunction((b) => document.querySelector('textarea').value !== b, before, { timeout: 20000 }).catch(() => {})
  await page.getByRole('button', { name: 'Dictate', exact: true }).waitFor({ timeout: 20000 })
  console.log(`dictation ${n} -> prompt: ${JSON.stringify(await prompt.inputValue())}`)
}

let failed = false
try {
  await dictate(1)
  await dictate(2)
} catch (e) {
  failed = true
  console.log('FAILED:', e.message.split('\n')[0])
  console.log('button now:', await page.getByRole('button', { name: /Dictate|Stop|Transcribing/ }).innerText().catch(() => '?'))
  const alert = await page.getByRole('alert').allInnerTexts()
  if (alert.length) console.log('alerts:', alert)
}
if (problems.length) console.log(problems.join('\n'))
await browser.close()
process.exit(failed ? 1 : 0)
