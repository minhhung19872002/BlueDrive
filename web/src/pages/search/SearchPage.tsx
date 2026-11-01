import { useSearchParams } from 'react-router-dom'
import PagePlaceholder from '@/components/PagePlaceholder'

export default function SearchPage() {
  const [params] = useSearchParams()
  const q = params.get('q')?.trim() ?? ''
  return q ? (
    <PagePlaceholder titleKey="menu.search" descriptionKey="empty.searchWithQuery" descriptionValues={{ q }} />
  ) : (
    <PagePlaceholder titleKey="menu.search" descriptionKey="empty.search" />
  )
}
