// Chat groups (#153) in a real Chromium: create a group, drag a chat into it, start a new chat inside it, change what it shares and
// isolate a chat. Usage: node groups.mjs (dev auth with header identities on :8088). Uses its own fresh identity.
import { chromium } from 'playwright'

const base = process.env.LOTS_URL ?? 'http://localhost:8088'
const user = `claude-test-groups-${Date.now()}`
const headers = { 'X-Dev-User': user, 'X-Dev-Roles': 'operator' }
const api = async (method, path, body) => {
  // Content-Type only with a body: FastEndpoints reads an empty JSON body as an error.
  const r = await fetch(base + path, { method, headers: body ? { ...headers, 'Content-Type': 'application/json' } : headers, body: body ? JSON.stringify(body) : undefined })
  return r.status === 204 ? null : r.json()
}
let failed = false
const ok = (c, m) => { console.log(`${c ? 'ok  ' : 'FAIL'} ${m}`); if (!c) failed = true }

// Two chats to organise (their runs may or may not finish; only the conversations matter here).
const chats = [crypto.randomUUID(), crypto.randomUUID()]
for (const [i, c] of chats.entries()) await api('POST', '/runs', { prompt: `group test chat ${i + 1}`, conversationId: c })

const browser = await chromium.launch()
const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } })
await ctx.addInitScript((u) => {
  localStorage.setItem('lots.dev-identity', JSON.stringify({ user: u, roles: 'operator' }))
  localStorage.setItem('lots.ui-language', 'en')
}, user)
const page = await ctx.newPage()
page.on('dialog', (d) => void d.accept(d.type() === 'prompt' ? 'Incident 42' : undefined))
await page.goto(`${base}/#/chat`)
await page.getByRole('button', { name: 'New group' }).click()
const group = page.locator('.chat-group', { hasText: 'Incident 42' })
await group.waitFor()
ok(true, 'a group is created from the sidebar')

const chat = page.locator('.chat-ungrouped li', { hasText: 'group test chat 1' })
await chat.dragTo(group.locator('summary'))
await page.waitForFunction(() => document.querySelector('.chat-group')?.textContent?.includes('group test chat 1'), null, { timeout: 5000 })
  .then(() => ok(true, 'dragging a chat onto the group moves it'), () => ok(false, 'dragging a chat onto the group moves it'))
const groups = await api('GET', '/groups')
ok(groups.length === 1 && groups[0].chats === 1, 'the server has the chat in the group')

await group.getByRole('button', { name: 'Group settings' }).click()
await group.getByLabel('Instructions for every chat in the group').fill('Answer briefly.')
await group.getByLabel('Chats in this group may use each other’s context').uncheck()
await group.getByRole('button', { name: 'Save' }).click()
await page.waitForTimeout(500)
const saved = (await api('GET', '/groups'))[0]
ok(saved.instructions === 'Answer briefly.' && saved.shareContext === false, 'group instructions and sharing are saved')

await page.goto(`${base}/#/chat?group=${saved.id}`)
await page.locator('#chat-prompt').fill('a new chat inside the group')
await page.getByRole('button', { name: 'Send' }).click()
await page.waitForURL(/#\/chat\/[0-9a-f-]{36}$/)
await page.waitForTimeout(500)
ok((await api('GET', '/groups'))[0].chats === 2, 'a chat started from the group joins it')

await page.evaluate(() => { document.querySelector('.chat-group').open = true }) // the folder may already be open (the active chat is in it)
const second = page.locator('.chat-list li', { hasText: 'group test chat 1' })
await second.locator('.chat-menu summary').click()
await second.getByLabel('Isolate this chat').check()
await page.waitForTimeout(500)
const listed = await api('GET', `/conversations?group=${saved.id}`)
ok(listed.conversations.some((c) => c.title.includes('group test chat 1') && c.isolated), 'a chat can be isolated from its menu')

await browser.close()
process.exit(failed ? 1 : 0)
