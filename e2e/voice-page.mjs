// Voice and personality page in a real Chromium: sliders, recording your own voice (fake microphone), consent, delete.
// The shell's /me endpoints are simulated so this runs without the voice service.
// Usage: node voice-page.mjs        (needs the web UI served on :8088)
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const fx = (f) => path.resolve(here, 'fixtures', f)
const base = process.env.LOTS_URL ?? 'http://localhost:8088'

const browser = await chromium.launch({
  args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${fx('real-question.wav')}`],
})
const page = await (await browser.newContext({ permissions: ['microphone'] })).newPage()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

const state = { talkativeness: 50, warmth: 50, formality: 50, expressiveness: 60, pace: 40, ownVoice: null, voiceEnabled: true }
const seen = { put: [], voice: [], deleted: 0 }
await page.route('**/me/settings', (r) => {
  if (r.request().method() === 'PUT') { seen.put.push(r.request().postDataJSON()); Object.assign(state, r.request().postDataJSON()) }
  r.fulfill({ json: state })
})
await page.route('**/me/voice', async (r) => {
  const m = r.request().method()
  if (m === 'PUT') {
    const body = r.request().postData() ?? ''
    seen.voice.push({ consent: /name="Consent"\r\n\r\ntrue/.test(body), hasAudio: /name="Audio"/.test(body) })
    state.ownVoice = { seconds: 14.2, consentAt: new Date().toISOString() }
  } else if (m === 'DELETE') { seen.deleted++; state.ownVoice = null }
  r.fulfill({ json: state })
})
await page.route('**/voice/vocabulary', (r) => r.fulfill({ json: { words: [], shared: [] } }))

await page.goto(`${base}/#/voice`)
await page.getByRole('heading', { name: 'Voice and personality' }).waitFor()
ok(true, 'the voice page opens from the menu route')

// Personality sliders: save is only enabled once something changed.
const save = page.getByRole('button', { name: 'Save', exact: true })
ok(await save.isDisabled(), 'Save is disabled before any change')
await page.getByLabel('Warmth').fill('80')
ok(await save.isEnabled(), 'changing a slider enables Save')
await save.click()
await page.getByRole('status').filter({ hasText: 'Saved' }).waitFor()
ok(seen.put.length === 1 && seen.put[0].warmth === 80 && seen.put[0].talkativeness === 50, 'the new value is sent to the server')

// Recording: Stop is locked until enough has been read; consent is required.
await page.getByRole('button', { name: 'Record my voice' }).click()
const stop = page.getByRole('button', { name: 'Stop' })
await stop.waitFor()
ok(await stop.isDisabled(), 'Stop is disabled until the minimum length')
ok(await page.getByLabel('Text to read').isVisible(), 'the text to read is shown')
await page.waitForFunction(() => !document.querySelector('button.primary[disabled]')?.textContent?.includes('Stop'), null, { timeout: 15000 })
await stop.click()
const use = page.getByRole('button', { name: 'Use this voice' })
await use.waitFor()
ok(await use.isDisabled(), '"Use this voice" is disabled without consent')
ok(await page.getByLabel('Your recording').isVisible(), 'the recording can be played back before saving')
await page.getByRole('checkbox').check()
await use.click()
await page.getByText('Your voice is active').waitFor()
ok(seen.voice.length === 1 && seen.voice[0].consent && seen.voice[0].hasAudio, 'recording and consent are sent together')

// Delete.
await page.getByRole('button', { name: 'Delete my voice' }).click()
await page.getByRole('button', { name: 'Record my voice' }).waitFor()
ok(seen.deleted === 1, 'deleting removes the voice')
await page.screenshot({ path: path.resolve(here, 'out/voice-page.png'), fullPage: true })

await browser.close()
process.exit(failed ? 1 : 0)
