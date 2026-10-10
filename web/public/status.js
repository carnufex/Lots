// Public status (#83): coarse components only, no internal names or errors. Refreshes every 30 s.
async function load() {
  try {
    const s = await (await fetch('/status', { cache: 'no-store' })).json()
    const label = { operational: 'All systems operational', degraded: 'Some features are degraded', down: 'Service disruption', unknown: 'Status unknown' }
    const o = document.getElementById('overall')
    o.textContent = label[s.overall] || s.overall
    o.className = 'overall ' + s.overall
    const ul = document.getElementById('components')
    ul.replaceChildren(...s.components.map(c => {
      const li = document.createElement('li')
      const name = document.createElement('span'); name.textContent = c.name
      const state = document.createElement('span'); state.textContent = c.state; state.className = c.state
      li.append(name, state)
      return li
    }))
    document.getElementById('checked').textContent = s.checkedAt ? 'Checked ' + new Date(s.checkedAt).toLocaleString() : ''
  } catch {
    document.getElementById('overall').textContent = 'Status unavailable'
  }
}
load()
setInterval(load, 30000)
