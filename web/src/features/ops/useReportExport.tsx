import { useCallback } from 'react'
import { App, Button } from 'antd'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { showApiError } from '@/api/errors'
import { exportReport, type ExportReportRequest } from '@/api/monitoring'
import { qko } from '@/api/queryKeys.ops'
import type { ReportJob } from '@/types/ops'

/**
 * Notify that a background report job was queued (spec §5): message + link to the reports center
 * where the job is highlighted (/reports?id=...).
 */
export function useReportQueuedNotice() {
  const { t } = useTranslation()
  const { notification } = App.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  return useCallback(
    (job: ReportJob) => {
      void queryClient.invalidateQueries({ queryKey: qko.reports })
      const key = `report-${job.id}`
      notification.info({
        key,
        message: job.title,
        description: t('ops.report.processing'),
        placement: 'topRight',
        duration: 8,
        actions: (
          <Button
            type="primary"
            size="small"
            onClick={() => {
              notification.destroy(key)
              navigate(`/reports?id=${job.id}`)
            }}
          >
            {t('ops.report.viewProgress')}
          </Button>
        ),
      })
    },
    [navigate, notification, queryClient, t],
  )
}

/** Reusable "export as background report" action: POST /reports/export then show the processing notice. */
export function useReportExport() {
  const notify = useReportQueuedNotice()
  return useCallback(
    async (request: ExportReportRequest): Promise<ReportJob | null> => {
      try {
        const job = await exportReport(request)
        notify(job)
        return job
      } catch (error) {
        showApiError(error)
        return null
      }
    },
    [notify],
  )
}
