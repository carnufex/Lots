// Conversation mode in a real Chromium: microphone on/off, listening -> hearing -> thinking -> speaking, and barge-in.
// The microphone is a timeline file (fixtures/conversation.wav); the shell's answers are simulated so the timing is deterministic.
// Usage: node conversation.mjs        (needs the stack on :8088 with voice enabled; run python make-fixtures.py first)
import { chromium } from 'playwright'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const fx = (f) => path.resolve(here, 'fixtures', f)
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const RUN = '11111111-2222-3333-4444-555555555555'

const browser = await chromium.launch({
  args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${fx('conversation.wav')}%noloop`, '--autoplay-policy=no-user-gesture-required'],
})
const ctx = await browser.newContext({ permissions: ['microphone'] })
const page = await ctx.newPage()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

const calls = { transcribe: 0, runs: 0, speak: 0, ack: 0, convIds: new Set(), voiceFlags: [] }
// This check covers push-per-utterance (the recording is sent when the browser hears the end); streaming is conversation-real.mjs.
await page.route('**/config', async (r) => {
  const res = await r.fetch()
  const json = await res.json()
  r.fulfill({ response: res, json: { ...json, voice: json.voice && { ...json.voice, streaming: false } } })
})
await page.route('**/voice/transcribe', (r) => { calls.transcribe++; r.fulfill({ json: { text: `fråga ${calls.transcribe}`, language: 'sv', durationSeconds: 1.9 } }) })
await page.route('**/voice/ack**', (r) => { calls.ack++; r.fulfill({ body: fs.readFileSync(fx('ack.wav')), contentType: 'audio/wav' }) })
await page.route('**/runs', (r) => {
  if (r.request().method() !== 'POST') return r.fallback()
  calls.runs++
  const body = r.request().postDataJSON()
  calls.convIds.add(body.conversationId)
  calls.voiceFlags.push(body.voice)
  r.fulfill({ status: 202, json: { id: RUN, status: 'Pending' } })
})
await page.route(`**/runs/${RUN}`, (r) => r.fulfill({ json: { id: RUN, prompt: 'x', status: 'Completed', finalAnswer: 'Hej! Det här är ett långt svar som tar en stund att läsa upp.', error: null, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), steps: [] } }))
await page.route(`**/runs/${RUN}/speak`, (r) => { calls.speak++; r.fulfill({ body: fs.readFileSync(fx('agent-long.wav')), contentType: 'audio/wav' }) })

// record every state change with a timestamp
await page.addInitScript(() => {
  window.__log = []
  const t0 = performance.now()
  new MutationObserver(() => {
    const el = document.querySelector('[data-testid="conversation-state"]')
    const s = el?.getAttribute('data-state')
    if (s && window.__log.at(-1)?.s !== s) window.__log.push({ s, t: Math.round(performance.now() - t0) })
  }).observe(document, { subtree: true, attributes: true, childList: true })
})

await page.goto(`${base}/#/chat`)
await page.getByRole('button', { name: 'Voice mode' }).click() // voice is a mode of the chat (#152)
const state = () => page.getByTestId('conversation-state').getAttribute('data-state')
ok((await state()) === 'off', 'starts outside a conversation')
ok(await page.getByRole('button', { name: 'Mute' }).count() === 0, 'no mute button before the conversation starts')

await page.getByRole('button', { name: 'Start conversation' }).click()
await page.waitForFunction(() => document.querySelector('[data-testid="conversation-state"]')?.dataset.state === 'listening', null, { timeout: 5000 })
ok(true, 'conversation started: listening')
await page.screenshot({ path: path.resolve(here, 'out/1-listening.png'), clip: await page.locator('.talk').boundingBox() })

const waitFor = (s, timeout = 20000) => page.waitForFunction((x) => window.__log.some((e) => e.s === x), s, { timeout })
await waitFor('hearing'); ok(true, 'hearing you while you speak')
await page.screenshot({ path: path.resolve(here, 'out/2-hearing.png'), clip: await page.locator('.talk').boundingBox() })
await waitFor('thinking'); ok(true, 'thinking after you stop')
await waitFor('speaking'); ok(true, 'speaking the answer')
await page.waitForTimeout(600)
await page.screenshot({ path: path.resolve(here, 'out/3-speaking.png'), clip: await page.locator('.talk').boundingBox() })

// The second utterance in the timeline arrives while the agent is still speaking (its answer is 15 s long): barge-in.
await page.waitForFunction(() => { const l = window.__log; const i = l.findIndex((e) => e.s === 'speaking'); return i >= 0 && l.slice(i + 1).some((e) => e.s === 'hearing') }, null, { timeout: 20000 })
ok(true, 'talking over the agent interrupts it (speaking -> hearing)')
await page.waitForFunction(() => { const l = window.__log; const i = l.findIndex((e) => e.s === 'speaking'); return l.slice(i + 1).some((e) => e.s === 'thinking') }, null, { timeout: 20000 })
ok(true, 'the interruption becomes a new turn')
await waitFor('speaking')
await page.waitForFunction(() => window.__log.filter((e) => e.s === 'speaking').length >= 2, null, { timeout: 20000 })
ok(true, 'the agent answers the interruption')

const lines = await page.locator('.transcript .line').allInnerTexts()
ok(lines.some((l) => l.includes('fråga 1')) && lines.some((l) => l.includes('fråga 2')), 'both turns are in the transcript')
ok(calls.transcribe === 2 && calls.runs === 2, `two turns sent (transcribe=${calls.transcribe}, runs=${calls.runs})`)
ok(calls.convIds.size === 1 && !calls.convIds.has(undefined) && calls.voiceFlags.every(Boolean), 'turns share one conversation id and are marked as voice runs')

// Mute is only a mute: the agent keeps talking and the conversation stays open.
const mute = page.getByRole('button', { name: 'Mute' })
await mute.click()
ok((await page.getByRole('button', { name: 'Unmute' }).getAttribute('aria-pressed')) === 'true', 'mute silences the microphone (button becomes Unmute)')
await page.waitForTimeout(500)
ok((await state()) !== 'off', 'muting does not end the conversation')
ok(await page.getByRole('button', { name: 'End conversation' }).isVisible(), 'a separate End conversation button is shown')
await page.getByRole('button', { name: 'Unmute' }).click()
ok(await page.getByRole('button', { name: 'Mute' }).isVisible(), 'unmute restores the microphone')

const states = await page.evaluate(() => window.__log.map((e) => `${e.s}@${e.t}`))
await page.getByRole('button', { name: 'End conversation' }).click()
// In Chat (#152) ending a new spoken conversation opens its thread: the voice turns are ordinary chat turns.
await page.waitForURL(/#\/chat\/[0-9a-f-]{36}$/, { timeout: 5000 })
ok(true, 'End conversation stops everything and opens the conversation in Chat')
ok(page.url().endsWith(`#/chat/${[...calls.convIds][0]}`), 'the chat opened is the conversation the spoken turns belong to')
await page.getByRole('button', { name: 'Voice mode' }).click()
ok(await page.getByRole('button', { name: 'Start conversation' }).isVisible(), 'you can talk again in the same conversation')
console.log('states:', states.join(' → '))
await browser.close()
process.exit(failed ? 1 : 0)
