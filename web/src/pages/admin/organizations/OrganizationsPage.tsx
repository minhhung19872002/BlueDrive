import { useState } from 'react'
import { Button, Space, Tabs } from 'antd'
import { ApartmentOutlined, PlusOutlined, TableOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { exportOrganizations } from '@/api/organizations'
import ExportButtons from '@/components/ExportButtons'
import PageHeader from '@/components/PageHeader'
import { useCan } from '@/hooks/usePermissions'
import { useServerTable } from '@/hooks/useServerTable'
import type { OrganizationDto, OrganizationFilter } from '@/types/admin'
import { openPrintView } from '@/utils/print'
import OrgFormModal from './OrgFormModal'
import OrgTableView from './OrgTableView'
import OrgTreeView from './OrgTreeView'

const INITIAL_FILTER: OrganizationFilter = {}

interface FormState {
  open: boolean
  editing: OrganizationDto | null
  parentId?: string | null
}

/** HSMT #1–3: organization structure as a draggable tree and as a filtered, server-paged table. */
export default function OrganizationsPage() {
  const { t } = useTranslation()
  const can = useCan()
  const table = useServerTable<OrganizationFilter>(INITIAL_FILTER)
  const [tab, setTab] = useState('tree')
  const [formState, setFormState] = useState<FormState>({ open: false, editing: null })

  const exportFilter = { ...table.filter, sort: table.params.sort }

  return (
    <>
      <PageHeader
        title={t('menu.organizations')}
        description={t('org.pageHint')}
        extra={
          <Space wrap>
            <ExportButtons
              onExport={(format) => exportOrganizations(exportFilter, format)}
              onPrint={() => openPrintView('/print/organizations', exportFilter)}
            />
            {can('org.upload') && (
              <Button type="primary" icon={<PlusOutlined />} onClick={() => setFormState({ open: true, editing: null })}>
                {t('org.create')}
              </Button>
            )}
          </Space>
        }
      />
      <Tabs
        activeKey={tab}
        onChange={setTab}
        items={[
          {
            key: 'tree',
            label: (
              <span>
                <ApartmentOutlined /> {t('org.tabTree')}
              </span>
            ),
            children: (
              <OrgTreeView
                onEdit={(org) => setFormState({ open: true, editing: org })}
                onAddChild={(parentId) => setFormState({ open: true, editing: null, parentId })}
              />
            ),
          },
          {
            key: 'table',
            label: (
              <span>
                <TableOutlined /> {t('org.tabTable')}
              </span>
            ),
            children: <OrgTableView table={table} onEdit={(org) => setFormState({ open: true, editing: org })} />,
          },
        ]}
      />
      <OrgFormModal
        open={formState.open}
        editing={formState.editing}
        defaultParentId={formState.parentId}
        onClose={() => setFormState({ open: false, editing: null })}
      />
    </>
  )
}
