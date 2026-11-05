import { useState } from "react";
import {
  Alert,
  Button,
  Checkbox,
  Form,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Select,
  Space,
  Table,
  Tag,
} from "antd";
import { AlertOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import {
  endEmergencyAccess,
  grantEmergencyAccess,
  listEmergencyAccess,
  type EmergencyAccess,
} from "@/api/offboarding";
import { listAllWorkspaces } from "@/api/workspaces";
import PageHeader from "@/components/PageHeader";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useCan } from "@/hooks/usePermissions";
import { formatDateTime } from "@/utils/format";

interface GrantForm {
  workspaceId: string;
  hours: number;
  reason: string;
  investigation: boolean;
}

/** Spec 8.5: an administrator's temporary access (≤ 24 h, a reason, audited, the owner told unless "điều tra"). */
export default function EmergencyAccessPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [form] = Form.useForm<GrantForm>();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const list = useQuery({
    queryKey: ["emergency-access"],
    queryFn: listEmergencyAccess,
  });
  const places = useQuery({
    queryKey: ["emergency-places", q],
    queryFn: () =>
      listAllWorkspaces({ q: q || undefined, page: 1, pageSize: 30 }),
    enabled: open,
  });
  const grant = useApiMutation(grantEmergencyAccess, {
    invalidate: [["emergency-access"]],
    successMessage: t("emergency.granted"),
    onSuccess: () => setOpen(false),
  });
  const end = useApiMutation(endEmergencyAccess, {
    invalidate: [["emergency-access"]],
    successMessage: t("emergency.ended"),
  });
  return (
    <>
      <PageHeader
        title={t("menu.emergencyAccess")}
        description={t("emergency.description")}
        extra={
          can("emergency.admin") && (
            <Button
              type="primary"
              danger
              icon={<AlertOutlined />}
              onClick={() => {
                form.resetFields();
                form.setFieldsValue({ hours: 4, investigation: false });
                setOpen(true);
              }}
            >
              {t("emergency.new")}
            </Button>
          )
        }
      />
      <Table<EmergencyAccess>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        pagination={{ pageSize: 20 }}
        scroll={{ x: "max-content" }}
        locale={{ emptyText: t("emergency.empty") }}
        columns={[
          {
            title: t("emergency.place"),
            key: "place",
            render: (_, r) =>
              r.active ? (
                <Link to={`/workspaces/${r.workspaceId}`}>
                  {r.workspaceName}
                </Link>
              ) : (
                r.workspaceName
              ),
          },
          { title: t("emergency.admin"), dataIndex: "adminName" },
          { title: t("emergency.reason"), dataIndex: "reason" },
          {
            title: t("emergency.time"),
            key: "time",
            render: (_, r) =>
              `${formatDateTime(r.createdAt)} → ${formatDateTime(r.endedAt ?? r.expiresAt)}`,
          },
          {
            title: t("emergency.status"),
            key: "status",
            render: (_, r) => (
              <Space size={4} wrap>
                {r.active ? (
                  <Tag color="red">{t("emergency.active")}</Tag>
                ) : (
                  <Tag>
                    {r.endedAt
                      ? t("emergency.endedBy", { name: r.endedByName ?? "—" })
                      : t("emergency.expired")}
                  </Tag>
                )}
                {r.investigation && (
                  <Tag color="purple">{t("emergency.investigation")}</Tag>
                )}
              </Space>
            ),
          },
          {
            title: "",
            key: "actions",
            render: (_, r) =>
              r.active &&
              can("emergency.admin") && (
                <Popconfirm
                  title={t("emergency.endConfirm")}
                  onConfirm={() => end.mutate(r.id)}
                >
                  <Button size="small">{t("emergency.end")}</Button>
                </Popconfirm>
              ),
          },
        ]}
      />
      {open && (
        <Modal
          open
          title={t("emergency.new")}
          okText={t("emergency.grant")}
          okButtonProps={{ danger: true, loading: grant.isPending }}
          onOk={() => form.submit()}
          onCancel={() => setOpen(false)}
          destroyOnHidden
        >
          <Alert
            type="warning"
            showIcon
            message={t("emergency.notice")}
            style={{ marginBottom: 12 }}
          />
          <Form<GrantForm>
            form={form}
            layout="vertical"
            onFinish={(v) => grant.mutate(v)}
          >
            <Form.Item
              name="workspaceId"
              label={t("emergency.place")}
              rules={[
                { required: true, message: t("emergency.placeRequired") },
              ]}
            >
              <Select
                showSearch
                filterOption={false}
                onSearch={setQ}
                loading={places.isFetching}
                options={(places.data?.items ?? []).map((w) => ({
                  value: w.id,
                  label:
                    w.type === "personal"
                      ? `Drive: ${w.name}`
                      : `Space: ${w.name}`,
                }))}
              />
            </Form.Item>
            <Form.Item
              name="hours"
              label={t("emergency.hours")}
              rules={[{ required: true }]}
            >
              <InputNumber
                min={1}
                max={24}
                precision={0}
                suffix={t("emergency.hoursUnit")}
              />
            </Form.Item>
            <Form.Item
              name="reason"
              label={t("emergency.reason")}
              rules={[
                {
                  required: true,
                  min: 10,
                  whitespace: true,
                  message: t("emergency.reasonRequired"),
                },
              ]}
            >
              <Input.TextArea rows={3} maxLength={1000} />
            </Form.Item>
            {can("investigation.admin") && (
              <Form.Item
                name="investigation"
                valuePropName="checked"
                extra={t("emergency.investigationHint")}
              >
                <Checkbox>{t("emergency.investigation")}</Checkbox>
              </Form.Item>
            )}
          </Form>
        </Modal>
      )}
    </>
  );
}
