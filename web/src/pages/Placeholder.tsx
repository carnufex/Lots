import { Icon } from '../components/Icon'
import type { Planned } from '../planned'
import { t } from '../i18n'

export default function PlaceholderPage({ item }: { item: Planned }) {
  return (
    <div className="placeholder">
      <Icon name={item.icon} size={56} />
      <h1>{item.label}</h1>
      <p className="muted">{item.blurb}</p>
      <p className="muted small">{t('Not built yet.')}</p>
    </div>
  )
}
