import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import vi from './locales/vi.json'
import viCollab from './locales/vi.collab.json'
import viOps from './locales/vi.ops.json'

type Dict = { [key: string]: string | Dict }

/** Feature areas keep their strings in separate files (collab = office/versions/discussion/search, ops = monitoring/dashboards/operations). */
function deepMerge(...sources: Dict[]): Dict {
  const out: Dict = {}
  for (const src of sources) {
    for (const [key, value] of Object.entries(src)) {
      const current = out[key]
      out[key] =
        typeof value === 'object' && typeof current === 'object' ? deepMerge(current, value) : value
    }
  }
  return out
}

void i18n.use(initReactI18next).init({
  resources: { vi: { translation: deepMerge(vi as Dict, viCollab as Dict, viOps as Dict) } },
  lng: 'vi',
  fallbackLng: 'vi',
  interpolation: { escapeValue: false },
})

export default i18n
