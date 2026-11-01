import { useState } from 'react'
import { Alert, App, Button, Modal, Space, Statistic, Table, Typography, Upload, type UploadFile } from 'antd'
import { CopyOutlined, DownloadOutlined, InboxOutlined } from '@ant-design/icons'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { downloadUserImportTemplate, importUsers } from '@/api/users'
import type { ImportedUser, ImportRowError, ImportUsersResult } from '@/types/admin'

interface UserImportModalProps {
  open: boolean
  onClose: () => void
}

const MAX_SIZE = 20 * 1024 * 1024

function ImportBody({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [result, setResult] = useState<{ dryRun: boolean; data: ImportUsersResult } | null>(null)
  const [busy, setBusy] = useState<'template' | 'check' | 'import' | null>(null)

  const run = async (dryRun: boolean) => {
    if (!file) return
    setBusy(dryRun ? 'check' : 'import')
    try {
      const data = await importUsers(file, dryRun)
      setResult({ dryRun, data })
      if (!dryRun && data.imported > 0) {
        void message.success(t('user.importDone', { count: data.imported }))
        await queryClient.invalidateQueries({ queryKey: qk.users })
      } else if (dryRun && data.errors.length === 0) {
        void message.success(t('user.importCheckOk', { count: data.total }))
      }
    } catch (error) {
      showApiError(error)
    } finally {
      setBusy(null)
    }
  }

  const downloadTemplate = async () => {
    setBusy('template')
    try {
      await downloadUserImportTemplate()
    } catch (error) {
      showApiError(error)
    } finally {
      setBusy(null)
    }
  }

  const copyPasswords = async (users: ImportedUser[]) => {
    const header = [t('user.importRow'), t('user.username'), t('user.fullName'), t('user.tempPassword')].join('\t')
    const lines = users.map((u) => [u.row, u.username, u.fullName, u.temporaryPassword ?? ''].join('\t'))
    try {
      await navigator.clipboard.writeText([header, ...lines].join('\n'))
      void message.success(t('user.importCopied'))
    } catch {
      void message.error(t('common.copyFailed'))
    }
  }

  const fileList: UploadFile[] = file ? [{ uid: 'import', name: file.name, status: 'done', size: file.size }] : []
  const data = result?.data
  const dryRun = result?.dryRun ?? false
  const imported = data && !dryRun ? data.users : []

  return (
    <Modal
      open
      title={t('user.importTitle')}
      width={820}
      onCancel={onClose}
      destroyOnHidden
      footer={
        <Space wrap>
          <Button onClick={onClose}>{t('common.close')}</Button>
          <Button disabled={!file} loading={busy === 'check'} onClick={() => void run(true)}>
            {t('user.importCheck')}
          </Button>
          <Button type="primary" disabled={!file || busy !== null} loading={busy === 'import'} onClick={() => void run(false)}>
            {t('user.importSubmit')}
          </Button>
        </Space>
      }
    >
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Alert
          type="info"
          showIcon
          message={t('user.importHint')}
          action={
            <Button size="small" icon={<DownloadOutlined />} loading={busy === 'template'} onClick={() => void downloadTemplate()}>
              {t('user.importTemplate')}
            </Button>
          }
        />
        <Upload.Dragger
          accept=".xlsx"
          maxCount={1}
          fileList={fileList}
          beforeUpload={(f) => {
            if (!f.name.toLowerCase().endsWith('.xlsx')) {
              void message.error(t('user.importOnlyXlsx'))
              return Upload.LIST_IGNORE
            }
            if (f.size > MAX_SIZE) {
              void message.error(t('user.importTooLarge'))
              return Upload.LIST_IGNORE
            }
            setFile(f)
            setResult(null)
            return false
          }}
          onRemove={() => {
            setFile(null)
            setResult(null)
          }}
        >
          <p className="ant-upload-drag-icon">
            <InboxOutlined />
          </p>
          <p className="ant-upload-text">{t('user.importDrop')}</p>
          <p className="ant-upload-hint">{t('user.importDropHint')}</p>
        </Upload.Dragger>

        {data && (
          <>
            <Space size="large" wrap>
              <Statistic title={t('user.importTotal')} value={data.total} />
              <Statistic title={t('user.importErrors')} value={data.errors.length} valueStyle={{ color: data.errors.length ? '#cf1322' : undefined }} />
              {!dryRun && <Statistic title={t('user.importImported')} value={data.imported} />}
            </Space>
            {data.errors.length > 0 ? (
              <>
                <Alert type="error" showIcon message={t('user.importHasErrors')} />
                <Table<ImportRowError>
                  size="small"
                  rowKey={(r, i) => `${r.row}-${i}`}
                  dataSource={data.errors}
                  pagination={{ pageSize: 10, hideOnSinglePage: true }}
                  scroll={{ x: 'max-content' }}
                  columns={[
                    { title: t('user.importRow'), dataIndex: 'row', width: 80 },
                    { title: t('user.importError'), dataIndex: 'message' },
                  ]}
                />
              </>
            ) : dryRun ? (
              <Alert type="success" showIcon message={t('user.importCheckOk', { count: data.total })} />
            ) : null}
            {imported.length > 0 && (
              <>
                <Space style={{ justifyContent: 'space-between', width: '100%' }} wrap>
                  <Typography.Text strong>{t('user.importedList')}</Typography.Text>
                  <Button icon={<CopyOutlined />} onClick={() => void copyPasswords(imported)}>
                    {t('user.importCopy')}
                  </Button>
                </Space>
                <Alert type="warning" showIcon message={t('user.tempPasswordHint')} />
                <Table<ImportedUser>
                  size="small"
                  rowKey="username"
                  dataSource={imported}
                  pagination={{ pageSize: 10, hideOnSinglePage: true }}
                  scroll={{ x: 'max-content' }}
                  columns={[
                    { title: t('user.importRow'), dataIndex: 'row', width: 80 },
                    { title: t('user.username'), dataIndex: 'username' },
                    { title: t('user.fullName'), dataIndex: 'fullName' },
                    {
                      title: t('user.tempPassword'),
                      dataIndex: 'temporaryPassword',
                      render: (v: string | null) =>
                        v ? <Typography.Text code copyable>{v}</Typography.Text> : t('user.importOwnPassword'),
                    },
                  ]}
                />
              </>
            )}
          </>
        )}
      </Space>
    </Modal>
  )
}

/** Bulk user import from the Excel template: dry-run check, then import (all-or-nothing). */
export default function UserImportModal({ open, onClose }: UserImportModalProps) {
  return open ? <ImportBody onClose={onClose} /> : null
}
