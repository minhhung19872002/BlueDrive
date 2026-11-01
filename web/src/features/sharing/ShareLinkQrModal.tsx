import { useEffect } from 'react'
import { Button, Modal, Result, Spin } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage } from '@/api/errors'
import { saveBlob } from '@/api/http'
import { getShareLinkQr } from '@/api/sharing'

/** QR code of a share link: the PNG is fetched with authentication and shown through an object URL. */
export default function ShareLinkQrModal({ linkId, onClose }: { linkId: string; onClose: () => void }) {
  const { t } = useTranslation()
  const qr = useQuery({
    queryKey: ['share-link-qr', linkId],
    queryFn: async () => {
      const blob = await getShareLinkQr(linkId)
      return { blob, url: URL.createObjectURL(blob) }
    },
    gcTime: 0,
    staleTime: Infinity,
    retry: 0,
  })
  const objectUrl = qr.data?.url
  useEffect(
    () => () => {
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    },
    [objectUrl],
  )

  return (
    <Modal
      open
      title={t('sharing.qrTitle')}
      onCancel={onClose}
      footer={[
        <Button key="close" onClick={onClose}>
          {t('common.close')}
        </Button>,
        <Button
          key="download"
          type="primary"
          icon={<DownloadOutlined />}
          disabled={!qr.data}
          onClick={() => qr.data && saveBlob(qr.data.blob, `qr-${linkId.slice(0, 8)}.png`)}
        >
          {t('sharing.qrDownload')}
        </Button>,
      ]}
      width={360}
    >
      <div style={{ display: 'flex', justifyContent: 'center', minHeight: 240, alignItems: 'center' }}>
        {qr.isLoading && <Spin />}
        {qr.isError && <Result status="warning" subTitle={getErrorMessage(qr.error)} />}
        {qr.data && <img src={qr.data.url} alt={t('sharing.qrTitle')} style={{ width: 240, height: 240 }} />}
      </div>
    </Modal>
  )
}
