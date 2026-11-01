import { Button, Card, Col, Descriptions, Form, Input, Row, Skeleton, Space, Tag } from 'antd'
import { useTranslation } from 'react-i18next'
import { updateProfile } from '@/api/auth'
import { qk } from '@/api/queryKeys'
import PageHeader from '@/components/PageHeader'
import StorageUsage from '@/components/StorageUsage'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useMe } from '@/hooks/useMe'
import type { AuthUser, UpdateProfileRequest } from '@/types/auth'
import { formatDateTime } from '@/utils/format'

const PHONE_PATTERN = /^[0-9+()\s.-]{6,20}$/

function ProfileForm({ me }: { me: AuthUser }) {
  const { t } = useTranslation()
  const [form] = Form.useForm<UpdateProfileRequest>()
  const mutation = useApiMutation(
    (v: UpdateProfileRequest) =>
      updateProfile({
        fullName: v.fullName.trim(),
        email: v.email?.trim() || null,
        phone: v.phone?.trim() || null,
        position: v.position?.trim() || null,
      }),
    { successMessage: t('profile.saved'), invalidate: [qk.me] },
  )

  return (
    <Form<UpdateProfileRequest>
      form={form}
      layout="vertical"
      initialValues={{ fullName: me.fullName, email: me.email, phone: me.phone, position: me.position }}
      onFinish={(v) => mutation.mutate(v)}
    >
      <Form.Item
        name="fullName"
        label={t('user.fullName')}
        rules={[{ required: true, whitespace: true, message: t('validation.fullNameRequired') }, { max: 255, message: t('validation.maxLength', { max: 255 }) }]}
      >
        <Input maxLength={255} />
      </Form.Item>
      <Form.Item name="email" label={t('user.email')} rules={[{ type: 'email', message: t('validation.emailInvalid') }]}>
        <Input maxLength={255} autoComplete="email" />
      </Form.Item>
      <Form.Item name="phone" label={t('user.phone')} rules={[{ pattern: PHONE_PATTERN, message: t('validation.phoneInvalid') }]}>
        <Input maxLength={20} autoComplete="tel" />
      </Form.Item>
      <Form.Item name="position" label={t('user.position')} rules={[{ max: 255, message: t('validation.maxLength', { max: 255 }) }]}>
        <Input maxLength={255} />
      </Form.Item>
      <Space>
        <Button type="primary" htmlType="submit" loading={mutation.isPending}>
          {t('common.save')}
        </Button>
        <Button onClick={() => form.resetFields()}>{t('common.reset')}</Button>
      </Space>
    </Form>
  )
}

export default function ProfilePage() {
  const { t } = useTranslation()
  const { data: me, isLoading, dataUpdatedAt } = useMe()

  return (
    <>
      <PageHeader title={t('header.profile')} description={t('profile.hint')} />
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={14}>
          <Card title={t('profile.editTitle')}>
            {isLoading || !me ? <Skeleton active /> : <ProfileForm key={dataUpdatedAt} me={me} />}
          </Card>
        </Col>
        <Col xs={24} lg={10}>
          <Card title={t('profile.accountTitle')}>
            {me ? (
              <Descriptions column={1} size="small">
                <Descriptions.Item label={t('user.username')}>{me.username}</Descriptions.Item>
                <Descriptions.Item label={t('user.organization')}>{me.organizationName || t('common.none')}</Descriptions.Item>
                <Descriptions.Item label={t('user.roles')}>
                  {me.roles.length ? me.roles.map((r) => <Tag key={r}>{r}</Tag>) : t('common.none')}
                </Descriptions.Item>
                <Descriptions.Item label={t('user.lastLogin')}>{formatDateTime(me.lastLoginAt) || t('common.none')}</Descriptions.Item>
              </Descriptions>
            ) : (
              <Skeleton active />
            )}
          </Card>
          <Card title={t('home.storageCard')} style={{ marginTop: 16 }}>
            <StorageUsage />
          </Card>
        </Col>
      </Row>
    </>
  )
}
