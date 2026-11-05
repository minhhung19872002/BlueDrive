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
  Tabs,
  Tag,
  Typography,
} from "antd";
import { DeleteOutlined, EditOutlined, PlusOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { listCatalogs, listCatalogItems } from "@/api/catalogs";
import { listLabels } from "@/api/labels";
import { listLibraries } from "@/api/libraries";
import {
  approveRetentionDelete,
  createHold,
  deleteRetentionPolicy,
  listHoldFolders,
  listHolds,
  listPreserved,
  listRetentionPolicies,
  listRetentionReview,
  purgePreserved,
  releaseHold,
  releasePreserved,
  saveRetentionPolicy,
  type HoldScope,
  type LegalHold,
  type PreservedItem,
  type RetentionPolicy,
  type RetentionScope,
  type ReviewItem,
  type SaveRetentionPolicy,
} from "@/api/retention";
import { listAllWorkspaces } from "@/api/workspaces";
import PageHeader from "@/components/PageHeader";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useCan } from "@/hooks/usePermissions";
import UserPicker from "@/pages/admin/userGroups/UserPicker";
import { formatBytes, formatDateTime } from "@/utils/format";

/** Space select over every workspace (administrators), searched on the server. */
function WorkspaceSelect({
  value,
  onChange,
  personal = false,
}: {
  value?: string;
  onChange?: (v: string) => void;
  personal?: boolean;
}) {
  const { t } = useTranslation();
  const [q, setQ] = useState("");
  const list = useQuery({
    queryKey: ["retention-workspaces", q, personal],
    queryFn: () =>
      listAllWorkspaces({
        q: q || undefined,
        type: personal ? undefined : "group",
        page: 1,
        pageSize: 30,
      }),
  });
  return (
    <Select
      showSearch
      filterOption={false}
      onSearch={setQ}
      value={value}
      onChange={onChange}
      loading={list.isFetching}
      placeholder={t("retention.pickSpace")}
      options={(list.data?.items ?? []).map((w) => ({
        value: w.id,
        label: w.name,
      }))}
      aria-label={t("retention.space")}
    />
  );
}

/** The target of a policy: a Space, a library of a Space, a label or a catalog item. */
function PolicyScopeFields({ kind }: { kind: RetentionScope }) {
  const { t } = useTranslation();
  const form = Form.useFormInstance<
    SaveRetentionPolicy & { catalogId?: string }
  >();
  const ws = Form.useWatch("workspaceId", form);
  const catalogId = Form.useWatch("catalogId", form);
  const labels = useQuery({
    queryKey: ["labels"],
    queryFn: listLabels,
    enabled: kind === "label",
  });
  const libraries = useQuery({
    queryKey: ["libraries", ws],
    queryFn: () => listLibraries(ws!),
    enabled: kind === "library" && !!ws,
  });
  const catalogs = useQuery({
    queryKey: ["catalogs"],
    queryFn: () => listCatalogs(),
    enabled: kind === "catalog",
  });
  const items = useQuery({
    queryKey: ["catalog-items", catalogId],
    queryFn: () => listCatalogItems(catalogId!, {}),
    enabled: kind === "catalog" && !!catalogId,
  });
  const required = [{ required: true, message: t("retention.scopeRequired") }];
  if (kind === "workspace")
    return (
      <Form.Item name="scopeId" label={t("retention.space")} rules={required}>
        <WorkspaceSelect />
      </Form.Item>
    );
  if (kind === "library")
    return (
      <>
        <Form.Item
          name="workspaceId"
          label={t("retention.space")}
          rules={required}
        >
          <WorkspaceSelect
            onChange={() => form.setFieldValue("scopeId", undefined)}
          />
        </Form.Item>
        <Form.Item
          name="scopeId"
          label={t("retention.library")}
          rules={required}
        >
          <Select
            options={(libraries.data ?? []).map((l) => ({
              value: l.id,
              label: l.name,
            }))}
            loading={libraries.isFetching}
            aria-label={t("retention.library")}
          />
        </Form.Item>
      </>
    );
  if (kind === "label")
    return (
      <Form.Item name="scopeId" label={t("labels.label")} rules={required}>
        <Select
          options={(labels.data ?? []).map((l) => ({
            value: l.id,
            label: <Tag color={l.color}>{l.name}</Tag>,
          }))}
          aria-label={t("labels.label")}
        />
      </Form.Item>
    );
  return (
    <>
      <Form.Item
        name="catalogId"
        label={t("retention.catalog")}
        rules={required}
      >
        <Select
          options={(catalogs.data ?? []).map((c) => ({
            value: c.id,
            label: c.name,
          }))}
          onChange={() => form.setFieldValue("scopeId", undefined)}
          aria-label={t("retention.catalog")}
        />
      </Form.Item>
      <Form.Item
        name="scopeId"
        label={t("retention.catalogItem")}
        rules={required}
      >
        <Select
          showSearch
          optionFilterProp="label"
          options={(items.data ?? []).map((i) => ({
            value: i.id,
            label: i.name,
          }))}
          loading={items.isFetching}
          aria-label={t("retention.catalogItem")}
        />
      </Form.Item>
    </>
  );
}

function PoliciesTab() {
  const { t } = useTranslation();
  const can = useCan();
  const admin = can("retention.admin");
  const [form] = Form.useForm<SaveRetentionPolicy & { catalogId?: string }>();
  const [editing, setEditing] = useState<RetentionPolicy | true | null>(null);
  const kind = Form.useWatch("scopeKind", form) ?? "workspace";
  const list = useQuery({
    queryKey: ["retention-policies"],
    queryFn: listRetentionPolicies,
  });
  const save = useApiMutation(
    (v: SaveRetentionPolicy) =>
      saveRetentionPolicy(editing === true ? null : editing!.id, v),
    {
      invalidate: [["retention-policies"]],
      successMessage: t("retention.policySaved"),
      onSuccess: () => setEditing(null),
    },
  );
  const remove = useApiMutation(deleteRetentionPolicy, {
    invalidate: [["retention-policies"]],
    successMessage: t("retention.policyDeleted"),
  });
  const open = (p: RetentionPolicy | true) => {
    setEditing(p);
    form.resetFields();
    form.setFieldsValue(
      p === true
        ? {
            name: "",
            description: "",
            scopeKind: "workspace",
            years: 5,
            basis: "created",
            onExpiry: "none",
            isActive: true,
          }
        : { ...p, workspaceId: p.workspaceId ?? undefined },
    );
  };
  return (
    <>
      {admin && (
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={() => open(true)}
          style={{ marginBottom: 12 }}
        >
          {t("retention.newPolicy")}
        </Button>
      )}
      <Table<RetentionPolicy>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        pagination={false}
        scroll={{ x: "max-content" }}
        locale={{ emptyText: t("retention.noPolicies") }}
        columns={[
          { title: t("retention.name"), dataIndex: "name" },
          { title: t("retention.scope"), dataIndex: "scopeName" },
          {
            title: t("retention.duration"),
            key: "years",
            render: (_, p) =>
              t("retention.yearsFrom", {
                years: p.years,
                basis: t(`retention.basis.${p.basis}`),
              }),
          },
          {
            title: t("retention.onExpiry"),
            key: "expiry",
            render: (_, p) => t(`retention.expiry.${p.onExpiry}`),
          },
          {
            title: t("labels.active"),
            key: "active",
            render: (_, p) => (p.isActive ? t("common.yes") : t("common.no")),
          },
          {
            title: "",
            key: "actions",
            render: (_, p) =>
              admin && (
                <Space>
                  <Button
                    size="small"
                    type="text"
                    icon={<EditOutlined />}
                    onClick={() => open(p)}
                    aria-label={t("common.edit")}
                  />
                  <Popconfirm
                    title={t("retention.deletePolicyConfirm", { name: p.name })}
                    okButtonProps={{ danger: true }}
                    onConfirm={() => remove.mutate(p.id)}
                  >
                    <Button
                      size="small"
                      type="text"
                      danger
                      icon={<DeleteOutlined />}
                      aria-label={t("common.delete")}
                    />
                  </Popconfirm>
                </Space>
              ),
          },
        ]}
      />
      {editing && (
        <Modal
          open
          title={
            editing === true
              ? t("retention.newPolicy")
              : t("retention.editPolicy")
          }
          okText={t("common.save")}
          okButtonProps={{ loading: save.isPending }}
          onOk={() => form.submit()}
          onCancel={() => setEditing(null)}
          destroyOnHidden
        >
          <Form
            form={form}
            layout="vertical"
            onFinish={(v) =>
              save.mutate({
                name: v.name,
                description: v.description,
                scopeKind: v.scopeKind,
                scopeId: v.scopeId,
                workspaceId: v.workspaceId ?? null,
                years: v.years,
                basis: v.basis,
                onExpiry: v.onExpiry,
                isActive: v.isActive,
              })
            }
          >
            <Form.Item
              name="name"
              label={t("retention.name")}
              rules={[{ required: true, whitespace: true }]}
            >
              <Input maxLength={200} />
            </Form.Item>
            <Form.Item name="description" label={t("labels.descriptionLabel")}>
              <Input.TextArea rows={2} maxLength={1000} />
            </Form.Item>
            <Form.Item name="scopeKind" label={t("retention.scopeKind")}>
              <Select
                onChange={() =>
                  form.setFieldsValue({
                    scopeId: undefined,
                    workspaceId: undefined,
                  })
                }
                options={(
                  ["workspace", "library", "label", "catalog"] as const
                ).map((k) => ({
                  value: k,
                  label: t(`retention.scopeKinds.${k}`),
                }))}
              />
            </Form.Item>
            <PolicyScopeFields kind={kind} />
            <Space wrap>
              <Form.Item
                name="years"
                label={t("retention.years")}
                rules={[{ required: true }]}
              >
                <InputNumber min={1} max={100} precision={0} />
              </Form.Item>
              <Form.Item name="basis" label={t("retention.basisLabel")}>
                <Select
                  style={{ minWidth: 180 }}
                  options={(["created", "modified"] as const).map((b) => ({
                    value: b,
                    label: t(`retention.basis.${b}`),
                  }))}
                />
              </Form.Item>
            </Space>
            <Form.Item name="onExpiry" label={t("retention.onExpiry")}>
              <Select
                options={(["none", "review", "delete"] as const).map((e) => ({
                  value: e,
                  label: t(`retention.expiry.${e}`),
                }))}
              />
            </Form.Item>
            <Form.Item name="isActive" valuePropName="checked">
              <Checkbox>{t("labels.active")}</Checkbox>
            </Form.Item>
          </Form>
        </Modal>
      )}
    </>
  );
}

function HoldsTab() {
  const { t } = useTranslation();
  const can = useCan();
  const admin = can("retention.admin");
  const [all, setAll] = useState(false);
  const [creating, setCreating] = useState(false);
  const [releasing, setReleasing] = useState<LegalHold | null>(null);
  const [reason, setReason] = useState("");
  const [form] = Form.useForm<{
    name: string;
    reason: string;
    scopeKind: HoldScope;
    workspaceId?: string;
    folderId?: string;
    users?: string[];
  }>();
  const kind = Form.useWatch("scopeKind", form) ?? "workspace";
  const ws = Form.useWatch("workspaceId", form);
  const folders = useQuery({
    queryKey: ["hold-folders", ws],
    queryFn: () => listHoldFolders(ws!),
    enabled: creating && kind === "folder" && !!ws,
  });
  const list = useQuery({
    queryKey: ["holds", all],
    queryFn: () => listHolds(all),
  });
  const create = useApiMutation(createHold, {
    invalidate: [["holds"]],
    successMessage: t("retention.holdCreated"),
    onSuccess: () => setCreating(false),
  });
  const release = useApiMutation(
    () => releaseHold(releasing!.id, reason.trim()),
    {
      invalidate: [["holds"]],
      successMessage: t("retention.holdReleased"),
      onSuccess: () => setReleasing(null),
    },
  );
  return (
    <>
      <Space style={{ marginBottom: 12 }} wrap>
        {admin && (
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => {
              form.resetFields();
              form.setFieldsValue({ scopeKind: "workspace" });
              setCreating(true);
            }}
          >
            {t("retention.newHold")}
          </Button>
        )}
        <Checkbox checked={all} onChange={(e) => setAll(e.target.checked)}>
          {t("retention.showReleased")}
        </Checkbox>
      </Space>
      <Table<LegalHold>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        pagination={false}
        scroll={{ x: "max-content" }}
        locale={{ emptyText: t("retention.noHolds") }}
        columns={[
          { title: t("retention.name"), dataIndex: "name" },
          { title: t("retention.scope"), dataIndex: "scopeName" },
          { title: t("retention.reason"), dataIndex: "reason" },
          {
            title: t("retention.placedBy"),
            key: "by",
            render: (_, h) =>
              `${h.createdByName ?? "—"} · ${formatDateTime(h.createdAt)}`,
          },
          {
            title: t("retention.status"),
            key: "status",
            render: (_, h) =>
              h.releasedAt ? (
                <Typography.Text type="secondary">
                  {t("retention.releasedAt", {
                    at: formatDateTime(h.releasedAt),
                    by: h.releasedByName ?? "—",
                  })}
                  {h.releaseReason ? ` — ${h.releaseReason}` : ""}
                </Typography.Text>
              ) : (
                <Tag color="red">{t("retention.holdActive")}</Tag>
              ),
          },
          {
            title: "",
            key: "actions",
            render: (_, h) =>
              admin &&
              !h.releasedAt && (
                <Button
                  size="small"
                  onClick={() => {
                    setReason("");
                    setReleasing(h);
                  }}
                >
                  {t("retention.release")}
                </Button>
              ),
          },
        ]}
      />
      {creating && (
        <Modal
          open
          title={t("retention.newHold")}
          okText={t("retention.placeHold")}
          okButtonProps={{ loading: create.isPending, danger: true }}
          onOk={() => form.submit()}
          onCancel={() => setCreating(false)}
          destroyOnHidden
        >
          <Form
            form={form}
            layout="vertical"
            onFinish={(v) =>
              create.mutate({
                name: v.name,
                reason: v.reason,
                scopeKind: v.scopeKind,
                scopeId:
                  (v.scopeKind === "workspace"
                    ? v.workspaceId
                    : v.scopeKind === "folder"
                      ? v.folderId
                      : v.users?.[v.users.length - 1]) ?? "",
              })
            }
          >
            <Alert
              type="warning"
              showIcon
              message={t("retention.holdNotice")}
              style={{ marginBottom: 12 }}
            />
            <Form.Item
              name="name"
              label={t("retention.name")}
              rules={[{ required: true, whitespace: true }]}
            >
              <Input maxLength={200} />
            </Form.Item>
            <Form.Item
              name="reason"
              label={t("retention.reason")}
              rules={[{ required: true, whitespace: true }]}
            >
              <Input.TextArea rows={2} maxLength={1000} />
            </Form.Item>
            <Form.Item name="scopeKind" label={t("retention.scopeKind")}>
              <Select
                options={(["workspace", "folder", "user"] as const).map(
                  (k) => ({ value: k, label: t(`retention.holdScopes.${k}`) }),
                )}
              />
            </Form.Item>
            {kind !== "user" && (
              <Form.Item
                name="workspaceId"
                label={t("retention.space")}
                rules={[
                  { required: true, message: t("retention.scopeRequired") },
                ]}
              >
                <WorkspaceSelect
                  personal
                  onChange={() => form.setFieldValue("folderId", undefined)}
                />
              </Form.Item>
            )}
            {kind === "folder" && (
              <Form.Item
                name="folderId"
                label={t("retention.folder")}
                rules={[
                  { required: true, message: t("retention.scopeRequired") },
                ]}
              >
                <Select
                  showSearch
                  optionFilterProp="label"
                  loading={folders.isFetching}
                  options={(folders.data ?? []).map((f) => ({
                    value: f.id,
                    label: f.path,
                  }))}
                  aria-label={t("retention.folder")}
                />
              </Form.Item>
            )}
            {kind === "user" && (
              <Form.Item
                name="users"
                label={t("retention.user")}
                rules={[
                  { required: true, message: t("retention.scopeRequired") },
                ]}
              >
                <UserPicker />
              </Form.Item>
            )}
          </Form>
        </Modal>
      )}
      {releasing && (
        <Modal
          open
          title={t("retention.releaseTitle", { name: releasing.name })}
          okText={t("retention.release")}
          okButtonProps={{
            loading: release.isPending,
            disabled: !reason.trim(),
          }}
          onOk={() => release.mutate(undefined)}
          onCancel={() => setReleasing(null)}
          destroyOnHidden
        >
          <Input.TextArea
            rows={3}
            maxLength={1000}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder={t("retention.releaseReason")}
            aria-label={t("retention.releaseReason")}
          />
        </Modal>
      )}
    </>
  );
}

function PreservedTab() {
  const { t } = useTranslation();
  const can = useCan();
  const admin = can("retention.admin");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const list = useQuery({
    queryKey: ["preserved", page, pageSize],
    queryFn: () => listPreserved({ page, pageSize }),
  });
  const back = useApiMutation(
    (i: PreservedItem) => releasePreserved([{ type: i.type, id: i.id }]),
    { invalidate: [["preserved"]], successMessage: t("retention.backToTrash") },
  );
  const purge = useApiMutation(
    (i: PreservedItem) => purgePreserved([{ type: i.type, id: i.id }]),
    { invalidate: [["preserved"]], successMessage: t("retention.purged") },
  );
  return (
    <>
      <Typography.Paragraph type="secondary">
        {t("retention.preservedHint")}
      </Typography.Paragraph>
      <Table<PreservedItem>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data?.items ?? []}
        scroll={{ x: "max-content" }}
        locale={{ emptyText: t("retention.noPreserved") }}
        pagination={{
          current: page,
          pageSize,
          total: list.data?.total ?? 0,
          onChange: (p, s) => {
            setPage(p);
            setPageSize(s);
          },
        }}
        columns={[
          {
            title: t("retention.item"),
            key: "name",
            render: (_, i) => `${i.type === "folder" ? "📁 " : ""}${i.name}`,
          },
          { title: t("retention.space"), dataIndex: "workspaceName" },
          {
            title: t("spaceTrash.size"),
            key: "size",
            render: (_, i) =>
              i.type === "file" ? formatBytes(i.sizeBytes) : "—",
          },
          {
            title: t("retention.deleted"),
            key: "deleted",
            render: (_, i) =>
              `${i.deletedByName ?? "—"} · ${i.deletedAt ? formatDateTime(i.deletedAt) : "—"}`,
          },
          {
            title: t("retention.keptBy"),
            key: "kept",
            render: (_, i) =>
              i.hold ? (
                <Tag color="red">{t("retention.heldBy", { name: i.hold })}</Tag>
              ) : i.retainUntil ? (
                t("retention.keptUntil", {
                  name: i.policyName,
                  at: formatDateTime(i.retainUntil),
                })
              ) : (
                <Tag>{t("retention.expired")}</Tag>
              ),
          },
          {
            title: "",
            key: "actions",
            render: (_, i) =>
              admin && (
                <Space>
                  <Button size="small" onClick={() => back.mutate(i)}>
                    {t("retention.toTrash")}
                  </Button>
                  {!i.hold && !i.retainUntil && (
                    <Popconfirm
                      title={t("retention.purgeConfirm", { name: i.name })}
                      okButtonProps={{ danger: true }}
                      onConfirm={() => purge.mutate(i)}
                    >
                      <Button size="small" danger>
                        {t("retention.purge")}
                      </Button>
                    </Popconfirm>
                  )}
                </Space>
              ),
          },
        ]}
      />
    </>
  );
}

function ReviewTab() {
  const { t } = useTranslation();
  const can = useCan();
  const [selected, setSelected] = useState<string[]>([]);
  const list = useQuery({
    queryKey: ["retention-review"],
    queryFn: listRetentionReview,
  });
  const approve = useApiMutation(() => approveRetentionDelete(selected), {
    invalidate: [["retention-review"]],
    successMessage: (r) => t("retention.approved", { count: r.deleted }),
    onSuccess: () => setSelected([]),
  });
  return (
    <>
      <Space style={{ marginBottom: 12 }} wrap>
        <Typography.Text type="secondary">
          {t("retention.reviewHint")}
        </Typography.Text>
        {can("retention.admin") && (
          <Popconfirm
            title={t("retention.approveConfirm", { count: selected.length })}
            okButtonProps={{ danger: true }}
            onConfirm={() => approve.mutate(undefined)}
            disabled={!selected.length}
          >
            <Button
              danger
              disabled={!selected.length}
              loading={approve.isPending}
            >
              {t("retention.approve")}
            </Button>
          </Popconfirm>
        )}
      </Space>
      <Table<ReviewItem>
        rowKey="fileId"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        scroll={{ x: "max-content" }}
        locale={{ emptyText: t("retention.noReview") }}
        rowSelection={
          can("retention.admin")
            ? {
                selectedRowKeys: selected,
                onChange: (k) => setSelected(k as string[]),
              }
            : undefined
        }
        columns={[
          { title: t("retention.item"), dataIndex: "name" },
          { title: t("retention.space"), dataIndex: "workspaceName" },
          { title: t("retention.policy"), dataIndex: "policyName" },
          {
            title: t("retention.expiredAt"),
            dataIndex: "expiredAt",
            render: (v: string) => formatDateTime(v),
          },
          {
            title: t("spaceTrash.size"),
            dataIndex: "sizeBytes",
            render: (v: number) => formatBytes(v),
          },
        ]}
      />
    </>
  );
}

/** Spec 7.2–7.3: retention policies, the preservation store, the review list and legal holds. */
export default function RetentionPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader
        title={t("menu.retention")}
        description={t("retention.description")}
      />
      <Tabs
        items={[
          {
            key: "policies",
            label: t("retention.tabs.policies"),
            children: <PoliciesTab />,
          },
          {
            key: "holds",
            label: t("retention.tabs.holds"),
            children: <HoldsTab />,
          },
          {
            key: "preserved",
            label: t("retention.tabs.preserved"),
            children: <PreservedTab />,
          },
          {
            key: "review",
            label: t("retention.tabs.review"),
            children: <ReviewTab />,
          },
        ]}
      />
    </>
  );
}
