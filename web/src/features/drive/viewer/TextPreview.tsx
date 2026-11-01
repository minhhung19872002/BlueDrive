import { Alert, Spin } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'

const MAX_BYTES = 1024 * 1024

/** Plain-text preview: fetches at most the first 1 MB through a Range request on the signed URL. */
export default function TextPreview({ url, sizeBytes }: { url: string; sizeBytes: number }) {
  const { t } = useTranslation()
  const text = useQuery({
    queryKey: ['text-preview', url],
    queryFn: async () => {
      const res = await fetch(url, { headers: { Range: `bytes=0-${MAX_BYTES - 1}` } })
      if (!res.ok) throw new Error(t('viewer.textFailed'))
      const buf = await res.arrayBuffer()
      return new TextDecoder('utf-8').decode(buf.slice(0, MAX_BYTES))
    },
    staleTime: Infinity,
    retry: 0,
  })
  if (text.isLoading) return <Spin />
  if (text.isError) return <Alert type="warning" showIcon message={t('viewer.textFailed')} />
  return (
    <div className="bd-text-preview">
      {sizeBytes > MAX_BYTES && <Alert type="info" showIcon message={t('viewer.textTruncated')} style={{ marginBottom: 8 }} />}
      <pre>{text.data}</pre>
    </div>
  )
}
