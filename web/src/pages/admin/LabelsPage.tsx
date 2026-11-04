import { useState } from "react";
import {
  Alert,
  Button,
  Checkbox,
  ColorPicker,
  Form,
  Input,
  InputNumber,
  Modal,
  Space,
  Table,
  Tag,
} from "antd";
import { EditOutlined, PlusOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { listLabels, saveLabel, type Label } from "@/api/labels";
import PageHeader from "@/components/PageHeader";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useCan } from "@/hooks/usePermissions";

type LabelForm = Omit<Label, "id">;

const RULES = [
  "blockExternal",
  "blockAnyoneLinks",
  "viewOnlyOutsideSpace",
  "watermark",
  "blockSync",
] as const;

/** Spec 7.1 "Nhãn phân loại": labels and what each one enforces. Not for information classified as state secret. */
export default function LabelsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const [form] = Form.useForm<LabelForm>();
  const [editing, setEditing] = useState<Label | true | null>(null);
  const labels = useQuery({ queryKey: ["labels"], queryFn: listLabels });
  const save = useApiMutation(
    (v: LabelForm) => saveLabel(editing === true ? null : editing!.id, v),
    {
      invalidate: [["labels"]],
      successMessage: t("labels.saved"),
      onSuccess: () => setEditing(null),
    },
  );
  const open = (l: Label | true) => {
    setEditing(l);
    form.setFieldsValue(
      l === true
        ? {
            name: "",
            color: "#1677ff",
            description: "",
            priority: 10,
            blockExternal: false,
            blockAnyoneLinks: false,
            viewOnlyOutsideSpace: false,
            watermark: false,
            blockSync: false,
            isActive: true,
          }
        : l,
    );
  };
  return (
    <>
      <PageHeader
        title={t("menu.labels")}
        description={t("labels.description")}
        extra={
          can("settings.edit") && (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => open(true)}
            >
              {t("labels.new")}
            </Button>
          )
        }
      />
      <Alert
        type="warning"
        showIcon
        message={t("labels.secretNotice")}
        style={{ marginBottom: 12 }}
      />
      <Table<Label>
        rowKey="id"
        loading={labels.isLoading}
        dataSource={labels.data ?? []}
        pagination={false}
        scroll={{ x: "max-content" }}
        columns={[
          {
            title: t("labels.label"),
            key: "name",
            render: (_, l) => <Tag color={l.color}>{l.name}</Tag>,
          },
          { title: t("labels.priority"), dataIndex: "priority" },
          { title: t("labels.descriptionLabel"), dataIndex: "description" },
          {
            title: t("labels.rules"),
            key: "rules",
            render: (_, l) => (
              <Space wrap size={[4, 4]}>
                {RULES.filter((r) => l[r]).map((r) => (
                  <Tag key={r}>{t(`labels.rule.${r}`)}</Tag>
                ))}
              </Space>
            ),
          },
          {
            title: t("labels.active"),
            key: "active",
            render: (_, l) => (l.isActive ? t("common.yes") : t("common.no")),
          },
          {
            title: "",
            key: "actions",
            render: (_, l) =>
              can("settings.edit") && (
                <Button
                  size="small"
                  type="text"
                  icon={<EditOutlined />}
                  onClick={() => open(l)}
                  aria-label={t("common.edit")}
                />
              ),
          },
        ]}
      />
      {editing && (
        <Modal
          open
          title={editing === true ? t("labels.new") : t("labels.edit")}
          okText={t("common.save")}
          okButtonProps={{ loading: save.isPending }}
          onOk={() => form.submit()}
          onCancel={() => setEditing(null)}
          destroyOnHidden
        >
          <Form<LabelForm>
            form={form}
            layout="vertical"
            onFinish={(v) =>
              save.mutate({
                ...v,
                color: typeof v.color === "string" ? v.color : String(v.color),
              })
            }
          >
            <Form.Item
              name="name"
              label={t("labels.label")}
              rules={[{ required: true, whitespace: true }]}
            >
              <Input maxLength={100} />
            </Form.Item>
            <Space wrap>
              <Form.Item
                name="color"
                label={t("labels.color")}
                getValueFromEvent={(c) => c.toHexString()}
              >
                <ColorPicker />
              </Form.Item>
              <Form.Item
                name="priority"
                label={t("labels.priority")}
                extra={t("labels.priorityHint")}
              >
                <InputNumber min={0} max={1000} precision={0} />
              </Form.Item>
            </Space>
            <Form.Item name="description" label={t("labels.descriptionLabel")}>
              <Input.TextArea rows={2} maxLength={1000} />
            </Form.Item>
            {RULES.map((r) => (
              <Form.Item
                key={r}
                name={r}
                valuePropName="checked"
                style={{ marginBottom: 4 }}
              >
                <Checkbox>{t(`labels.rule.${r}`)}</Checkbox>
              </Form.Item>
            ))}
            <Form.Item name="isActive" valuePropName="checked">
              <Checkbox>{t("labels.active")}</Checkbox>
            </Form.Item>
          </Form>
        </Modal>
      )}
    </>
  );
}
