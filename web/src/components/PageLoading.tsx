import { Spin } from 'antd'
import { useTranslation } from 'react-i18next'

export default function PageLoading() {
  const { t } = useTranslation()
  return (
    <div className="bd-page-loading">
      <Spin size="large" tip={t('common.loading')}>
        <div style={{ width: 120, height: 80 }} />
      </Spin>
    </div>
  )
}
