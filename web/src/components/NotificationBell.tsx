import { useCallback, useState } from 'react'
import { App, Badge, Button, Empty, List, Popover, Space, Spin, Typography } from 'antd'
import { BellOutlined, CheckOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { qk } from '@/api/queryKeys'
import { getUnreadNotificationCount, listNotifications, markNotificationsRead } from '@/api/system'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useNotificationHub } from '@/hooks/useNotificationHub'
import type { NotificationDto } from '@/types/admin'
import type { Paged } from '@/types/common'
import { formatRelative } from '@/utils/format'

const PAGE_SIZE = 10

/** Header bell: unread badge (polled every 60s), latest notifications, realtime push via SignalR. */
export default function NotificationBell() {
  const { t } = useTranslation()
  const { notification } = App.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)

  const unread = useQuery({
    queryKey: qk.unreadCount,
    queryFn: getUnreadNotificationCount,
    refetchInterval: 60_000,
    staleTime: 30_000,
  })
  const listKey = [...qk.notificationList, PAGE_SIZE]
  const list = useQuery({
    queryKey: listKey,
    queryFn: () => listNotifications({ page: 1, pageSize: PAGE_SIZE }),
    enabled: open,
  })

  const markAll = useApiMutation(() => markNotificationsRead([]), { invalidate: [qk.notifications] })
  const markOne = useApiMutation((id: string) => markNotificationsRead([id]), { invalidate: [qk.notifications] })

  const onPush = useCallback(
    (n: NotificationDto) => {
      queryClient.setQueryData<number>(qk.unreadCount, (c) => (c ?? 0) + 1)
      queryClient.setQueryData<Paged<NotificationDto>>([...qk.notificationList, PAGE_SIZE], (old) =>
        old ? { ...old, total: old.total + 1, items: [n, ...old.items].slice(0, PAGE_SIZE) } : old,
      )
      notification.info({ message: n.title, description: n.body ?? undefined, placement: 'topRight' })
    },
    [notification, queryClient],
  )
  useNotificationHub(onPush, true)

  const openItem = (n: NotificationDto) => {
    if (!n.isRead) markOne.mutate(n.id)
    if (n.link) {
      setOpen(false)
      if (/^https?:\/\//i.test(n.link)) window.open(n.link, '_blank', 'noopener')
      else navigate(n.link)
    }
  }

  const items = list.data?.items ?? []
  const count = unread.data ?? 0

  const content = (
    <div className="bd-notif-panel">
      {list.isLoading ? (
        <div style={{ textAlign: 'center', padding: 24 }}>
          <Spin />
        </div>
      ) : items.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('header.noNotifications')} />
      ) : (
        <List<NotificationDto>
          size="small"
          dataSource={items}
          renderItem={(n) => (
            <List.Item
              className={n.isRead ? 'bd-notif-item' : 'bd-notif-item bd-notif-unread'}
              onClick={() => openItem(n)}
              style={{ cursor: n.link || !n.isRead ? 'pointer' : 'default' }}
            >
              <List.Item.Meta
                title={<Typography.Text strong={!n.isRead}>{n.title}</Typography.Text>}
                description={
                  <Space direction="vertical" size={0}>
                    {n.body && (
                      <Typography.Paragraph type="secondary" ellipsis={{ rows: 2 }} style={{ margin: 0 }}>
                        {n.body}
                      </Typography.Paragraph>
                    )}
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {formatRelative(n.createdAt)}
                    </Typography.Text>
                  </Space>
                }
              />
            </List.Item>
          )}
        />
      )}
    </div>
  )

  return (
    <Popover
      trigger="click"
      placement="bottomRight"
      open={open}
      onOpenChange={setOpen}
      title={
        <div className="bd-notif-title">
          <span>{t('header.notifications')}</span>
          <Button
            type="link"
            size="small"
            icon={<CheckOutlined />}
            disabled={count === 0}
            loading={markAll.isPending}
            onClick={() => markAll.mutate()}
          >
            {t('notification.markAllRead')}
          </Button>
        </div>
      }
      content={content}
    >
      <Badge count={count} size="small" overflowCount={99}>
        <Button
          type="text"
          shape="circle"
          aria-label={t('notification.ariaLabel', { count })}
          icon={<BellOutlined />}
        />
      </Badge>
    </Popover>
  )
}
