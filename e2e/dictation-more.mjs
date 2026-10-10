// More dictation scenarios: keyboard shortcut, three in a row, a failed transcription followed by a retry.
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const here = path.dirname(fileURLToPath(import.meta.url))
const wav = path.resolve(here, 'fixtures/hallo-sv.wav')
const browser = await chromium.launch({ args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${wav}`] })
const page = await (await browser.newContext({ permissions: ['microphone'] })).newPage()
await page.goto('http://localhost:8088/#/chat')
await page.getByLabel('Speech language').selectOption({ label: 'Svenska' })
const prompt = page.locator('textarea')
const label = () => page.getByRole('button', { name: /Dictate|Stop|Transcribing/ }).innerText()

// 1. keyboard: Alt+M start, Alt+M stop
await page.keyboard.press('Alt+m'); await page.waitForTimeout(3000); console.log('after Alt+M: button =', await label())
await page.keyboard.press('Alt+m'); await page.getByRole('button', { name: 'Dictate', exact: true }).waitFor({ timeout: 20000 })
console.log('1 (keyboard):', JSON.stringify(await prompt.inputValue()))

// 2. two more with the mouse, immediately after each other
for (const n of [2, 3]) {
  await page.getByRole('button', { name: 'Dictate', exact: true }).click(); await page.waitForTimeout(3000)
  await page.getByRole('button', { name: 'Stop', exact: true }).click(); await page.getByRole('button', { name: 'Dictate', exact: true }).waitFor({ timeout: 20000 })
  console.log(`${n}:`, (await prompt.inputValue()).split('?').length - 1, 'phrases in prompt')
}

// 3. voice service down: error, then it must be usable again
await page.route('**/voice/transcribe', (r) => r.fulfill({ status: 503, body: '{}' }))
await page.getByRole('button', { name: 'Dictate', exact: true }).click(); await page.waitForTimeout(2500)
await page.getByRole('button', { name: 'Stop', exact: true }).click(); await page.getByRole('alert').first().waitFor({ timeout: 10000 })
console.log('503 case: alert =', await page.getByRole('alert').first().innerText(), '| button =', await label())
await page.unroute('**/voice/transcribe')
await page.getByRole('button', { name: 'Dictate', exact: true }).click(); await page.waitForTimeout(3000)
await page.getByRole('button', { name: 'Stop', exact: true }).click(); await page.getByRole('button', { name: 'Dictate', exact: true }).waitFor({ timeout: 20000 })
console.log('after recovery:', (await prompt.inputValue()).split('?').length - 1, 'phrases; alert shown:', await page.getByRole('alert').count())
await browser.close()
