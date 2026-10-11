// Meetings page (#42) in a real Chromium: upload a two-speaker recording, wait for the transcript, name a speaker, search,
// play from a line, export, delete. Needs the stack with the voice service (meetings endpoint and diarization models).
// The recording: python e2e/make-meeting.py e2e/fixtures <voice-url>/v1 <key>   (two synthetic voices, git-ignored).
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const user = `claude-test-meetings-${Date.now()}`
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

const browser = await chromium.launch()
const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true })
await ctx.addInitScript((u) => {
  localStorage.setItem('lots.dev-identity', JSON.stringify({ user: u, roles: 'operator' }))
  localStorage.setItem('lots.ui-language', 'en')
}, user)
const page = await ctx.newPage()
await page.goto(`${base}/#/meetings`)
await page.getByLabel('Recording').setInputFiles(path.join(here, 'fixtures', 'meeting.wav'))
await page.getByLabel('Title').fill('Weekly operations')
await page.getByLabel('Speakers (if known)').fill('2')
await page.getByRole('button', { name: 'Upload' }).click()
await page.getByRole('link', { name: 'Weekly operations' }).click()
await page.locator('.meeting-lines li').first().waitFor({ timeout: 120000 })
ok(true, 'the uploaded meeting is transcribed')
const labels = await page.locator('.meeting-lines strong').allInnerTexts()
ok(new Set(labels).size === 2, `two speakers told apart (${[...new Set(labels)].join(', ')})`)

await page.getByPlaceholder('Speaker 1').fill('Anna')
await page.getByRole('button', { name: 'Save names' }).click()
await page.locator('.meeting-lines strong', { hasText: 'Anna' }).first().waitFor()
ok(true, 'a speaker can be named')

await page.getByLabel('Search the transcript').fill('säkerhetskopiorna')
ok((await page.locator('.meeting-lines li').count()) === 1, 'search narrows the transcript')
await page.getByLabel('Search the transcript').fill('')

await page.locator('.meeting-lines li button').nth(1).click()
await page.locator('audio.meeting-player').waitFor()
await page.waitForFunction(() => { const a = document.querySelector('audio.meeting-player'); return a && a.currentTime > 4 }, null, { timeout: 10000 })
  .then(() => ok(true, 'a line plays the recording from its time'), () => ok(false, 'a line plays the recording from its time'))

const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'SRT' }).click()])
ok(download.suggestedFilename().endsWith('.srt'), 'export as SRT')

page.once('dialog', (d) => void d.accept())
await page.getByRole('button', { name: 'Delete the meeting' }).click()
await page.waitForURL(/#\/meetings$/)
ok(!(await page.getByRole('link', { name: 'Weekly operations' }).isVisible()), 'the meeting can be deleted')

await browser.close()
process.exit(failed ? 1 : 0)
