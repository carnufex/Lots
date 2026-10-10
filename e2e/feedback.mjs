// Feedback loop (#121) in a real Chromium: rate an answer in the chat with a comment, see it in History, then as a reviewer find it in
// the queue and turn it into an eval case. Needs the stack on :8088 with dev header identities and a model that answers.
// Usage: node feedback.mjs
import { chromium } from 'playwright'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'

const here = path.dirname(fileURLToPath(import.meta.url))
const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const out = path.resolve(here, 'out')
fs.mkdirSync(out, { recursive: true })
const stamp = Date.now().toString(36)

const browser = await chromium.launch()
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }
const as = async (user, roles) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  await context.addInitScript(([u, r]) => {
    localStorage.setItem('lots.dev-identity', JSON.stringify({ user: u, roles: r }))
    localStorage.setItem('lots.ui-language', 'en')
  }, [user, roles])
  return context.newPage()
}

// A user asks and rates the answer.
const page = await as('claude-test-feedback', 'operator')
await page.goto(`${base}/#/chat`)
const question = `Is lots-postgres-1 healthy? (${stamp})`
await page.locator('#chat-prompt').fill(question)
await page.keyboard.press('Enter')
const bad = page.getByRole('button', { name: 'Bad answer' }).last()
await bad.waitFor({ timeout: 60000 })
await bad.click()
await page.getByPlaceholder('What was wrong, and what did you expect?').fill('It did not check the container status.')
await page.getByRole('button', { name: 'Save', exact: true }).click()
await page.getByRole('button', { name: 'Edit comment' }).waitFor()
ok((await bad.getAttribute('aria-pressed')) === 'true', 'thumbs down is pressed after rating')
await page.screenshot({ path: path.join(out, 'feedback-chat.png') })

await page.reload()
await page.getByRole('button', { name: 'Bad answer' }).last().waitFor()
ok((await page.getByRole('button', { name: 'Bad answer' }).last().getAttribute('aria-pressed')) === 'true', 'the rating is still there after a reload')
const conversation = /chat\/([0-9a-f-]{36})/.exec(page.url())?.[1]
if (conversation) {
  await page.goto(`${base}/#/history/${conversation}`)
  await page.getByText('You rated this answer bad').waitFor({ timeout: 10000 })
  ok(await page.getByText('It did not check the container status.').isVisible(), 'History shows the rating and the comment')
} else ok(false, 'the chat has a conversation id in the URL')

await page.goto(`${base}/#/feedback`)
ok(await page.getByText(/only available to reviewers/).waitFor({ timeout: 10000 }).then(() => true, () => false), 'an operator does not get the review queue')

// A reviewer turns it into an eval case.
const admin = await as('claude-test-reviewer', 'admin')
await admin.goto(`${base}/#/feedback`)
const card = admin.locator('li.card', { hasText: stamp })
await card.waitFor({ timeout: 10000 })
ok(await card.getByText('It did not check the container status.').isVisible(), 'the reviewer sees the comment in the queue')
await card.getByRole('button', { name: 'Make eval case' }).click()
await card.getByLabel('Question (remove personal data: it goes into a dataset file)').fill('Is lots-postgres-1 healthy?')
await card.getByLabel('Expected tools (comma separated)').fill('list_containers')
await card.getByLabel('What a correct answer does (judge criteria)').fill('Checks the container list and reports the health of lots-postgres-1.')
await card.getByRole('button', { name: 'Save eval case' }).click()
await admin.locator('li.card', { hasText: stamp }).waitFor({ state: 'detached', timeout: 10000 }) // leaves the open queue
await admin.getByLabel('State').selectOption('Converted')
const converted = admin.locator('li.card', { hasText: stamp })
await converted.waitFor({ timeout: 10000 })
ok(/eval case fb-[0-9a-f]{8}/.test(await converted.innerText()), 'the converted item shows its eval case id')
await admin.screenshot({ path: path.join(out, 'feedback-queue.png'), fullPage: true })

const dataset = await admin.evaluate(async () => {
  const id = JSON.parse(localStorage.getItem('lots.dev-identity'))
  const r = await fetch('/feedback/eval-cases', { headers: { 'X-Dev-User': id.user, 'X-Dev-Roles': id.roles } })
  return r.json()
})
ok(dataset.cases.some((c) => c.question === 'Is lots-postgres-1 healthy?' && c.expectedTools?.[0] === 'list_containers'), 'the eval dataset export contains the case')

await browser.close()
process.exit(failed ? 1 : 0)
