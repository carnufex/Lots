import { useCallback, useEffect, useState } from 'react'
import type { Api, ChatGroup, ConversationSummary } from '../api'
import type { ProfileInfo } from '../config'
import { fmt, t } from '../i18n'

const DRAG = 'application/x-lots-chat'

/**
 * The chat sidebar with groups (#153): folders of the user's chats. Drag a chat onto a group (or use its menu), start a new chat inside
 * a group, and set what the group shares. Chats in a group see each other's context unless the chat or the group opts out.
 */
export default function ChatList({ api, list, activeId, profiles, onChanged }: {
  api: Api
  list: ConversationSummary[]
  activeId?: string
  profiles: ProfileInfo[]
  onChanged: () => void
}) {
  const [groups, setGroups] = useState<ChatGroup[]>([])
  const [filter, setFilter] = useState('')
  const [editing, setEditing] = useState<string | null>(null)
  const [showArchived, setShowArchived] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(() => void api.groups().then(setGroups, () => setGroups([])), [api])
  useEffect(load, [load])

  const act = async (p: Promise<unknown>) => {
    setError(null)
    try {
      await p
    } catch (e) {
      setError(String(e))
    }
    load()
    onChanged()
  }
  const move = (chat: string, group: string | null) => void act(api.organise(chat, group ? { groupId: group } : { ungroup: true }))
  const newGroup = () => {
    const name = window.prompt(t('Name of the new group'))
    if (name?.trim()) void act(api.createGroup({ name: name.trim() }))
  }

  const f = filter.trim().toLowerCase()
  const shown = list.filter((c) => !f || c.title.toLowerCase().includes(f) || (c.summary ?? '').toLowerCase().includes(f))
  const visibleGroups = groups.filter((g) => showArchived || !g.archived)
  const drop = (group: string | null) => ({
    onDragOver: (e: React.DragEvent) => e.dataTransfer.types.includes(DRAG) && e.preventDefault(),
    onDrop: (e: React.DragEvent) => {
      const chat = e.dataTransfer.getData(DRAG)
      if (chat) move(chat, group)
    },
  })

  const item = (c: ConversationSummary) => (
    <li
      key={c.id}
      className={c.id === activeId ? 'active' : undefined}
      draggable
      onDragStart={(e) => e.dataTransfer.setData(DRAG, c.id)}
    >
      <a href={`#/chat/${c.id}`} title={c.summary ?? c.title}>
        {c.pinned && <span className="muted small" title={t('Pinned')}>📌 </span>}
        {c.voice && <span className="muted small">🎙 </span>}
        {c.title}
      </a>
      <span className="muted small">
        {fmt.date(c.endedAt)}
        {c.isolated && <span title={t('Isolated: this chat neither reads nor shares group context')}> · {t('isolated')}</span>}
      </span>
      <ChatMenu chat={c} groups={groups} onMove={(g) => move(c.id, g)} onSet={(o) => void act(api.organise(c.id, o))} />
    </li>
  )

  return (
    <aside className="chat-list" aria-label={t('Conversations')}>
      <div className="row">
        <a className="btn primary small" href="#/chat">
          {t('New chat')}
        </a>
        <button type="button" className="btn small" onClick={newGroup}>
          {t('New group')}
        </button>
      </div>
      <input className="chat-filter" aria-label={t('Filter chats')} placeholder={t('Filter chats')} value={filter} onChange={(e) => setFilter(e.target.value)} />
      {error && <p className="error small">{error}</p>}
      {visibleGroups.map((g) => (
        <details key={g.id} className="chat-group" open={editing === g.id || g.pinned || shown.some((c) => c.groupId === g.id && c.id === activeId) || undefined}>
          <summary {...drop(g.id)} style={g.color ? { borderLeftColor: g.color } : undefined}>
            <span className="grow">
              {g.name} <span className="muted small">{g.chats}</span>
              {!g.shareContext && <span className="muted small" title={t('Chats in this group do not share context')}> · {t('not shared')}</span>}
            </span>
            <a className="btn ghost small" href={`#/chat?group=${g.id}`} title={t('New chat in this group')} aria-label={t('New chat in this group')}>
              +
            </a>
            <button type="button" className="btn ghost small" aria-label={t('Group settings')} aria-expanded={editing === g.id}
              onClick={(e) => { e.preventDefault(); setEditing(editing === g.id ? null : g.id) }}>
              ⚙
            </button>
          </summary>
          {editing === g.id && (
            <GroupSettings
              group={g}
              profiles={profiles}
              onSave={(patch) => void act(api.updateGroup(g.id, patch)).then(() => setEditing(null))}
              onDelete={() => window.confirm(t('Delete the group "{name}"? Its chats stay, ungrouped.', { name: g.name })) && void act(api.deleteGroup(g.id))}
            />
          )}
          <ul>{shown.filter((c) => c.groupId === g.id).map(item)}</ul>
        </details>
      ))}
      <div className="chat-ungrouped" {...drop(null)}>
        {groups.length > 0 && <div className="navtitle">{t('Chats')}</div>}
        <ul>{shown.filter((c) => !c.groupId || !groups.some((g) => g.id === c.groupId)).map(item)}</ul>
      </div>
      {groups.some((g) => g.archived) && (
        <button type="button" className="btn ghost small" onClick={() => setShowArchived((v) => !v)}>
          {showArchived ? t('Hide archived groups') : t('Show archived groups')}
        </button>
      )}
    </aside>
  )
}

function ChatMenu({ chat, groups, onMove, onSet }: {
  chat: ConversationSummary
  groups: ChatGroup[]
  onMove: (group: string | null) => void
  onSet: (o: { isolated?: boolean; pinned?: boolean; archived?: boolean }) => void
}) {
  const [isolated, setIsolated] = useState(chat.isolated) // shown at once; the list reloads from the server afterwards
  return (
    <details className="chat-menu">
      <summary className="btn ghost small" aria-label={t('Chat options')}>…</summary>
      <div className="viewas-panel card" role="menu">
        <label>
          {t('Group')}
          <select value={chat.groupId ?? ''} onChange={(e) => onMove(e.target.value || null)}>
            <option value="">{t('No group')}</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </select>
        </label>
        <label className="check">
          <input type="checkbox" checked={isolated} onChange={(e) => { setIsolated(e.target.checked); onSet({ isolated: e.target.checked }) }} /> {t('Isolate this chat')}
        </label>
        <button type="button" className="btn small" onClick={() => onSet({ pinned: !chat.pinned })}>
          {chat.pinned ? t('Unpin') : t('Pin')}
        </button>
        <button type="button" className="btn small" onClick={() => onSet({ archived: !chat.archived })}>
          {chat.archived ? t('Unarchive') : t('Archive')}
        </button>
      </div>
    </details>
  )
}

function GroupSettings({ group, profiles, onSave, onDelete }: {
  group: ChatGroup
  profiles: ProfileInfo[]
  onSave: (patch: Partial<ChatGroup>) => void
  onDelete: () => void
}) {
  const [draft, setDraft] = useState({
    name: group.name,
    color: group.color ?? '#2dd4bf',
    instructions: group.instructions ?? '',
    defaultContext: group.defaultContext ?? '',
    shareContext: group.shareContext,
    pinned: group.pinned,
    archived: group.archived,
  })
  return (
    <form
      className="group-settings card"
      onSubmit={(e) => {
        e.preventDefault()
        onSave(draft)
      }}
    >
      <label>
        {t('Name')}
        <input value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} required maxLength={100} />
      </label>
      <label>
        {t('Colour')}
        <input type="color" value={draft.color} onChange={(e) => setDraft({ ...draft, color: e.target.value })} />
      </label>
      <label>
        {t('Instructions for every chat in the group')}
        <textarea rows={3} maxLength={2000} value={draft.instructions} placeholder={t('e.g. We are working on incident 42; answer briefly.')}
          onChange={(e) => setDraft({ ...draft, instructions: e.target.value })} />
      </label>
      {profiles.length > 1 && (
        <label>
          {t('Default context')}
          <select value={draft.defaultContext} onChange={(e) => setDraft({ ...draft, defaultContext: e.target.value })}>
            <option value="">{t('Automatic')}</option>
            {profiles.map((p) => (
              <option key={p.name} value={p.name}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
      )}
      <label className="check">
        <input type="checkbox" checked={draft.shareContext} onChange={(e) => setDraft({ ...draft, shareContext: e.target.checked })} />{' '}
        {t('Chats in this group may use each other’s context')}
      </label>
      <label className="check">
        <input type="checkbox" checked={draft.pinned} onChange={(e) => setDraft({ ...draft, pinned: e.target.checked })} /> {t('Pin to the top')}
      </label>
      <label className="check">
        <input type="checkbox" checked={draft.archived} onChange={(e) => setDraft({ ...draft, archived: e.target.checked })} /> {t('Archived')}
      </label>
      <div className="row">
        <button type="submit" className="btn primary small">
          {t('Save')}
        </button>
        <button type="button" className="btn small" onClick={onDelete}>
          {t('Delete group')}
        </button>
      </div>
    </form>
  )
}
