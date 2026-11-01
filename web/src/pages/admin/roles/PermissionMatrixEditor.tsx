import { useMemo, useState } from 'react'
import { Alert, App, Button, Card, Checkbox, Empty, Result, Select, Skeleton, Space, Table, Tag, Typography } from 'antd'
import { CopyOutlined, SaveOutlined, UndoOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import type { ColumnsType } from 'antd/es/table'
import { useTranslation } from 'react-i18next'
import { getErrorMessage, showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { ADMIN_ROLE_CODE, getRolePermissions, setRolePermissions } from '@/api/roles'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useRoles } from '@/hooks/useLookups'
import { useCan } from '@/hooks/usePermissions'
import type { MatrixFunction, RoleDto, RolePermissionMatrix } from '@/types/admin'

type MatrixRow =
  | { key: string; kind: 'module'; module: string; moduleName: string; functions: MatrixFunction[] }
  | { key: string; kind: 'function'; fn: MatrixFunction }

const codeOf = (fn: string, action: string) => `${fn}.${action}`

interface CheckState {
  checked: boolean
  indeterminate: boolean
}

function stateOf(codes: string[], granted: Set<string>): CheckState {
  const n = codes.filter((c) => granted.has(c)).length
  return { checked: codes.length > 0 && n === codes.length, indeterminate: n > 0 && n < codes.length }
}

interface EditorProps {
  role: RoleDto
  matrix: RolePermissionMatrix
  readOnly: boolean
}

function MatrixTable({ role, matrix, readOnly }: EditorProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const roles = useRoles()
  const [granted, setGranted] = useState(() => new Set(matrix.granted))
  const [copying, setCopying] = useState(false)
  const [copySource, setCopySource] = useState<string | undefined>()

  const allCodes = useMemo(
    () => matrix.functions.flatMap((f) => f.availableActions.map((a) => codeOf(f.function, a))),
    [matrix.functions],
  )
  const initial = useMemo(() => new Set(matrix.granted), [matrix.granted])
  const dirty = granted.size !== initial.size || [...granted].some((c) => !initial.has(c))

  const rows = useMemo<MatrixRow[]>(() => {
    const out: MatrixRow[] = []
    const modules = new Map<string, MatrixFunction[]>()
    matrix.functions.forEach((f) => modules.set(f.module, [...(modules.get(f.module) ?? []), f]))
    modules.forEach((fns, module) => {
      out.push({ key: `m:${module}`, kind: 'module', module, moduleName: fns[0].moduleName, functions: fns })
      fns.forEach((fn) => out.push({ key: `f:${fn.function}`, kind: 'function', fn }))
    })
    return out
  }, [matrix.functions])

  const save = useApiMutation((codes: string[]) => setRolePermissions(role.id, codes), {
    successMessage: t('role.permissionsSaved'),
    invalidate: [qk.roles, qk.permissionComparison, qk.me],
  })

  const toggle = (codes: string[], on: boolean) =>
    setGranted((prev) => {
      const next = new Set(prev)
      codes.forEach((c) => (on ? next.add(c) : next.delete(c)))
      return next
    })

  const copyFrom = async (sourceId: string) => {
    setCopySource(sourceId)
    setCopying(true)
    try {
      const src = await getRolePermissions(sourceId)
      const valid = new Set(allCodes)
      setGranted(new Set(src.granted.filter((c) => valid.has(c))))
      void message.info(t('role.copiedHint', { name: src.roleName }))
    } catch (error) {
      showApiError(error)
    } finally {
      setCopying(false)
    }
  }

  const fnCodes = (fn: MatrixFunction) => fn.availableActions.map((a) => codeOf(fn.function, a))
  const columnCodes = (action: string) =>
    matrix.functions.filter((f) => f.availableActions.includes(action)).map((f) => codeOf(f.function, action))

  const columns: ColumnsType<MatrixRow> = [
    {
      title: t('role.function'),
      key: 'function',
      fixed: 'left',
      width: 240,
      render: (_, row) =>
        row.kind === 'module' ? (
          <Typography.Text strong className="bd-matrix-module">
            {row.moduleName}
          </Typography.Text>
        ) : (
          <Space direction="vertical" size={0}>
            <Typography.Text>{row.fn.functionName}</Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {row.fn.function}
            </Typography.Text>
          </Space>
        ),
    },
    {
      title: (
        <Checkbox
          {...stateOf(allCodes, granted)}
          disabled={readOnly}
          onChange={(e) => toggle(allCodes, e.target.checked)}
        >
          {t('role.allRow')}
        </Checkbox>
      ),
      key: 'all',
      align: 'center',
      width: 110,
      render: (_, row) => {
        const codes = row.kind === 'module' ? row.functions.flatMap(fnCodes) : fnCodes(row.fn)
        return (
          <Checkbox
            {...stateOf(codes, granted)}
            disabled={readOnly}
            aria-label={row.kind === 'module' ? row.moduleName : row.fn.functionName}
            onChange={(e) => toggle(codes, e.target.checked)}
          />
        )
      },
    },
    ...matrix.actions.map((action) => {
      const codes = columnCodes(action.code)
      return {
        title: (
          <Space direction="vertical" size={2} align="center">
            <Checkbox
              {...stateOf(codes, granted)}
              disabled={readOnly || codes.length === 0}
              aria-label={action.name}
              onChange={(e) => toggle(codes, e.target.checked)}
            />
            <span>{action.name}</span>
          </Space>
        ),
        key: action.code,
        align: 'center' as const,
        width: 100,
        render: (_: unknown, row: MatrixRow) => {
          if (row.kind === 'module') {
            const moduleCodes = row.functions
              .filter((f) => f.availableActions.includes(action.code))
              .map((f) => codeOf(f.function, action.code))
            return moduleCodes.length ? (
              <Checkbox
                {...stateOf(moduleCodes, granted)}
                disabled={readOnly}
                aria-label={`${row.moduleName} - ${action.name}`}
                onChange={(e) => toggle(moduleCodes, e.target.checked)}
              />
            ) : null
          }
          if (!row.fn.availableActions.includes(action.code)) {
            return <Typography.Text type="secondary">—</Typography.Text>
          }
          const code = codeOf(row.fn.function, action.code)
          return (
            <Checkbox
              checked={granted.has(code)}
              disabled={readOnly}
              aria-label={`${row.fn.functionName} - ${action.name}`}
              onChange={(e) => toggle([code], e.target.checked)}
            />
          )
        },
      }
    }),
  ]

  return (
    <Card
      size="small"
      title={
        <Space wrap>
          <span>{t('role.matrixTitle', { name: role.name })}</span>
          <Tag color="blue">{t('role.grantedCount', { count: granted.size, total: allCodes.length })}</Tag>
        </Space>
      }
      extra={
        !readOnly && (
          <Space wrap>
            <Select
              size="small"
              style={{ width: 220 }}
              placeholder={
                <span>
                  <CopyOutlined /> {t('role.copyFrom')}
                </span>
              }
              value={copySource}
              loading={copying || roles.isLoading}
              onChange={(id: string) => void copyFrom(id)}
              options={roles.data?.filter((r) => r.id !== role.id).map((r) => ({ value: r.id, label: r.name }))}
            />
            <Button
              size="small"
              icon={<UndoOutlined />}
              disabled={!dirty}
              onClick={() => {
                setGranted(new Set(initial))
                setCopySource(undefined)
              }}
            >
              {t('role.undo')}
            </Button>
            <Button
              size="small"
              type="primary"
              icon={<SaveOutlined />}
              disabled={!dirty}
              loading={save.isPending}
              onClick={() => save.mutate([...granted].sort())}
            >
              {t('common.save')}
            </Button>
          </Space>
        )
      }
    >
      {readOnly && (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={role.code === ADMIN_ROLE_CODE ? t('role.adminReadOnly') : t('role.noEditPermission')}
        />
      )}
      {dirty && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('role.unsaved')} />}
      <Table<MatrixRow>
        rowKey="key"
        size="small"
        columns={columns}
        dataSource={rows}
        pagination={false}
        sticky
        scroll={{ x: 240 + 110 + matrix.actions.length * 100 }}
        rowClassName={(row) => (row.kind === 'module' ? 'bd-matrix-module-row' : '')}
      />
    </Card>
  )
}

/** HSMT #8: permission matrix (functions grouped by module × 7 actions) for one role. */
export default function PermissionMatrixEditor({ role }: { role: RoleDto | undefined }) {
  const { t } = useTranslation()
  const can = useCan()
  const query = useQuery({
    queryKey: qk.rolePermissions(role?.id ?? ''),
    queryFn: () => getRolePermissions(role!.id),
    enabled: !!role,
    staleTime: 0,
  })

  if (!role) {
    return (
      <Card size="small">
        <Empty description={t('role.selectHint')} />
      </Card>
    )
  }
  if (query.isLoading) {
    return (
      <Card size="small">
        <Skeleton active paragraph={{ rows: 10 }} />
      </Card>
    )
  }
  if (query.isError || !query.data) {
    return (
      <Card size="small">
        <Result status="warning" title={getErrorMessage(query.error)} />
      </Card>
    )
  }

  const readOnly = role.code === ADMIN_ROLE_CODE || !can('role.admin')
  const matrix =
    role.code === ADMIN_ROLE_CODE
      ? {
          ...query.data,
          granted: query.data.functions.flatMap((f) => f.availableActions.map((a) => codeOf(f.function, a))),
        }
      : query.data
  return <MatrixTable key={`${role.id}-${query.dataUpdatedAt}`} role={role} matrix={matrix} readOnly={readOnly} />
}
