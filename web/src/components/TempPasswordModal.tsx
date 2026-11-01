import { Alert, Modal, Typography } from 'antd'
import { useTranslation } from 'react-i18next'

interface TempPasswordModalProps {
  open: boolean
  username?: string
  password?: string | null
  onClose: () => void
}

/** Shows a one-time temporary password with a copy button. */
export default function TempPasswordModal({ open, username, password, onClose }: TempPasswordModalProps) {
  const { t } = useTranslation()
  return (
    <Modal
      open={open}
      title={t('user.tempPasswordTitle')}
      onCancel={onClose}
      onOk={onClose}
      okText={t('common.close')}
      cancelButtonProps={{ style: { display: 'none' } }}
      destroyOnHidden
    >
      <Alert type="warning" showIcon message={t('user.tempPasswordHint')} style={{ marginBottom: 16 }} />
      {username && (
        <Typography.Paragraph>
          {t('user.username')}: <Typography.Text strong>{username}</Typography.Text>
        </Typography.Paragraph>
      )}
      <Typography.Paragraph>
        {t('user.tempPassword')}:{' '}
        <Typography.Text code copyable={{ tooltips: [t('common.copy'), t('common.copied')] }} style={{ fontSize: 16 }}>
          {password}
        </Typography.Text>
      </Typography.Paragraph>
    </Modal>
  )
}
