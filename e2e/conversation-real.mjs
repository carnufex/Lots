// Conversation mode against the real stack (no mocks): fake microphone plays one utterance, we time the phases.
// Usage: node conversation-real.mjs [fixtures/some.wav]     (the file should contain ~2 s silence, one utterance, then silence)
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const here = path.dirname(fileURLToPath(import.meta.url))
const wav = path.resolve(here, process.argv[2] ?? 'fixtures/real-question.wav')
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const browser = await chromium.launch({ args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${wav}%noloop`, '--autoplay-policy=no-user-gesture-required'] })
const page = await (await browser.newContext({ permissions: ['microphone'] })).newPage()
const sockets = []
page.on('websocket', (ws) => { sockets.push(ws.url()); ws.on('framereceived', (f) => typeof f.payload === 'string' && f.payload.includes('"final"') && console.log('stream final:', f.payload)) })
await page.addInitScript(() => {
  window.__log = []; const t0 = performance.now()
  new MutationObserver(() => { const s = document.querySelector('[data-testid="conversation-state"]')?.getAttribute('data-state'); if (s && window.__log.at(-1)?.s !== s) window.__log.push({ s, t: Math.round(performance.now() - t0) }) }).observe(document, { subtree: true, attributes: true, childList: true })
})
await page.goto(`${base}/#/chat`)
await page.getByRole('button', { name: 'Voice mode' }).click() // voice is a mode of the chat (#152)
await page.getByRole('button', { name: 'Start conversation' }).click()
await page.waitForFunction(() => window.__log.some((e) => e.s === 'speaking'), null, { timeout: 90000 })
await page.waitForTimeout(1500)
const log = await page.evaluate(() => window.__log)
const at = (s) => log.find((e) => e.s === s)?.t
console.log('states:', log.map((e) => `${e.s}@${e.t}`).join(' → '))
console.log(`speech ended -> speaking: ${at('speaking') - at('thinking')} ms (thinking started when the utterance ended, incl. the 700 ms end-of-speech wait is before that)`)
console.log('streaming sockets:', sockets.map((u) => u.replace(/ticket=[^&]+/, 'ticket=…')).join(', ') || 'none')
console.log((await page.locator('.transcript .line').allInnerTexts()).map((l) => l.replace(/\s+/g, ' ')).join('\n'))
await browser.close()
