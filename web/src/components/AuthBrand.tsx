import { Typography } from 'antd'
import { useTranslation } from 'react-i18next'
import { useBranding } from '@/hooks/useLookups'

/** Logo + application/organization name for the public auth pages (from GET /auth/branding). */
export default function AuthBrand() {
  const { t } = useTranslation()
  const { data } = useBranding()
  const appName = data?.appName || t('common.appName')
  return (
    <div className="bd-auth-brand">
      <img src={data?.logoUrl || '/logo.svg'} alt="" width={56} height={56} style={{ objectFit: 'contain' }} />
      <Typography.Title level={3} style={{ margin: '12px 0 0' }}>
        {appName}
      </Typography.Title>
      <Typography.Text type="secondary">{data?.orgName || t('common.appSlogan')}</Typography.Text>
    </div>
  )
}
