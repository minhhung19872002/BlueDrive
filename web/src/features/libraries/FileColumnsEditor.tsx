import { useState } from 'react'
import { Button, Divider, Form, Space, Typography } from 'antd'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getFileColumns, setFileColumns, type ColumnValue } from '@/api/libraries'
import { useApiMutation } from '@/hooks/useApiMutation'
import { ColumnValueInput } from './ColumnValueInput'
import { columnText } from './columnText'

/** Spec 11.4: the library's metadata columns of a file in the details panel, editable by who may edit it. */
export default function FileColumnsEditor({ fileId, canEdit }: { fileId: string; canEdit: boolean }) {
  const { t } = useTranslation()
  const key = ['file-columns', fileId]
  const columns = useQuery({ queryKey: key, queryFn: () => getFileColumns(fileId) })
  const [editing, setEditing] = useState(false)
  const [values, setValues] = useState<Record<string, ColumnValue>>({})
  const startEditing = () => {
    setValues(Object.fromEntries((columns.data ?? []).map((c) => [c.column.key, c.value])))
    setEditing(true)
  }
  const save = useApiMutation(() => setFileColumns([fileId], values), {
    invalidate: [key, ['library-items']],
    successMessage: t('library.columnsSaved'),
    onSuccess: () => setEditing(false),
  })
  if (!columns.data || columns.data.length === 0) return null
  return (
    <>
      <Divider orientation="left" orientationMargin={0} style={{ marginTop: 8 }}>
        {t('library.columnsTitle')}
      </Divider>
      {editing ? (
        <Form layout="vertical" size="small" onFinish={() => save.mutate(undefined)}>
          {columns.data.map(({ column }) => (
            <Form.Item key={column.key} label={column.name} required={column.required}>
              <ColumnValueInput column={column} value={values[column.key] ?? null} onChange={(v) => setValues((s) => ({ ...s, [column.key]: v }))} />
            </Form.Item>
          ))}
          <Space>
            <Button type="primary" htmlType="submit" size="small" loading={save.isPending}>
              {t('common.save')}
            </Button>
            <Button size="small" onClick={() => setEditing(false)}>
              {t('common.cancel')}
            </Button>
          </Space>
        </Form>
      ) : (
        <>
          {columns.data.map(({ column, value }) => (
            <div key={column.key} style={{ display: 'flex', gap: 8, marginBottom: 4 }}>
              <Typography.Text type="secondary" style={{ width: 110, flex: 'none' }}>
                {column.name}
              </Typography.Text>
              <Typography.Text>{columnText(column, value, t('common.yes'), t('common.no')) || '—'}</Typography.Text>
            </div>
          ))}
          {canEdit && (
            <Button size="small" type="link" style={{ padding: 0 }} onClick={startEditing}>
              {t('library.editColumns')}
            </Button>
          )}
        </>
      )}
    </>
  )
}
