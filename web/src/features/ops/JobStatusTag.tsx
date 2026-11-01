import { Tag } from 'antd'
import { useTranslation } from 'react-i18next'
import type { JobStatus } from '@/types/ops'
import { JOB_STATUS_COLORS } from './utils'

/** Colored tag for a background job status (reports, backups). */
export default function JobStatusTag({ status }: { status: JobStatus }) {
  const { t } = useTranslation()
  return <Tag color={JOB_STATUS_COLORS[status]}>{t(`ops.jobStatus.${status}`)}</Tag>
}
