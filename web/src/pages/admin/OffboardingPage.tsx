import { useState } from "react";
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Descriptions,
  Empty,
  List,
  Popconfirm,
  Space,
  Steps,
  Table,
  Tabs,
  Tag,
  TreeSelect,
  Typography,
} from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  changeUserUnit,
  exportOffboarding,
  getOffboardingPreview,
  listOffboardings,
  offboardUser,
  type OffboardingReport,
  type UnitChangeReport,
} from "@/api/offboarding";
import ExportButtons from "@/components/ExportButtons";
import PageHeader from "@/components/PageHeader";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useOrgTree } from "@/hooks/useLookups";
import UserPicker from "@/pages/admin/userGroups/UserPicker";
import { formatBytes, formatDateTime } from "@/utils/format";
import { filterTreeNodeUnaccent, orgTreeToSelect } from "@/utils/tree";

/** One user out of UserPicker (multi-select by design): the last one chosen. */
function OneUser({
  value,
  onChange,
}: {
  value: string | null;
  onChange: (v: string | null) => void;
}) {
  return (
    <UserPicker
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[ids.length - 1] ?? null)}
    />
  );
}

function Report({ report }: { report: OffboardingReport }) {
  const { t } = useTranslation();
  return (
    <Card
      size="small"
      title={t("offboarding.reportTitle", { name: report.userName })}
      extra={
        <ExportButtons
          onExport={(format) => exportOffboarding(report.id, format)}
        />
      }
    >
      <Descriptions size="small" column={1} style={{ marginBottom: 8 }}>
        <Descriptions.Item label={t("offboarding.recipient")}>
          {report.recipientName}
        </Descriptions.Item>
        <Descriptions.Item
          label={t("offboarding.performed")}
        >{`${report.performedByName} · ${formatDateTime(report.performedAt)}`}</Descriptions.Item>
      </Descriptions>
      <List
        size="small"
        dataSource={report.lines}
        renderItem={(l) => (
          <List.Item>
            <Typography.Text strong>{l.item}</Typography.Text>: {l.detail}
          </List.Item>
        )}
      />
    </Card>
  );
}

function OffboardTab() {
  const { t } = useTranslation();
  const [userId, setUserId] = useState<string | null>(null);
  const [recipientId, setRecipientId] = useState<string | null>(null);
  const [revokeLinks, setRevokeLinks] = useState(false);
  const [report, setReport] = useState<OffboardingReport | null>(null);
  const preview = useQuery({
    queryKey: ["offboarding-preview", userId],
    queryFn: () => getOffboardingPreview(userId!),
    enabled: !!userId,
  });
  const run = useApiMutation(
    () =>
      offboardUser(userId!, {
        recipientId: recipientId!,
        revokeLinks,
        deactivate: true,
      }),
    {
      invalidate: [["offboardings"], ["users"]],
      successMessage: t("offboarding.done"),
      onSuccess: (r) => setReport(r),
    },
  );
  const p = preview.data;
  const sole = p?.spaces.filter((s) => s.soleOwner) ?? [];
  const step = !userId ? 0 : !recipientId ? 1 : report ? 3 : 2;
  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Steps
        size="small"
        current={step}
        items={[
          { title: t("offboarding.step1") },
          { title: t("offboarding.step2") },
          { title: t("offboarding.step3") },
          { title: t("offboarding.step4") },
        ]}
      />
      <div>
        <Typography.Text strong>{t("offboarding.user")}</Typography.Text>
        <OneUser
          value={userId}
          onChange={(v) => {
            setUserId(v);
            setReport(null);
          }}
        />
      </div>
      {p && (
        <Card
          size="small"
          title={t("offboarding.whatTheyHave", { name: p.fullName })}
        >
          <Descriptions size="small" column={{ xs: 1, sm: 2 }}>
            <Descriptions.Item label={t("offboarding.drive")}>
              {t("offboarding.driveCount", {
                files: p.files,
                folders: p.folders,
                size: formatBytes(p.bytes),
              })}
            </Descriptions.Item>
            <Descriptions.Item label={t("offboarding.links")}>
              {p.activeLinks}
            </Descriptions.Item>
            <Descriptions.Item label={t("offboarding.groups")}>
              {p.groups.length ? p.groups.join(", ") : "—"}
            </Descriptions.Item>
            <Descriptions.Item label={t("offboarding.spaces")}>
              <Space wrap size={[4, 4]}>
                {p.spaces.length
                  ? p.spaces.map((s) => (
                      <Tag key={s.id} color={s.soleOwner ? "red" : undefined}>
                        {s.name} · {s.role}
                      </Tag>
                    ))
                  : "—"}
              </Space>
            </Descriptions.Item>
          </Descriptions>
          {sole.length > 0 && (
            <Alert
              type="warning"
              showIcon
              message={t("offboarding.soleOwner", {
                names: sole.map((s) => s.name).join(", "),
              })}
            />
          )}
        </Card>
      )}
      {p && (
        <div>
          <Typography.Text strong>{t("offboarding.recipient")}</Typography.Text>
          <OneUser value={recipientId} onChange={setRecipientId} />
          <Typography.Paragraph type="secondary" style={{ marginTop: 4 }}>
            {t("offboarding.recipientHint", { name: p.fullName })}
          </Typography.Paragraph>
          <Checkbox
            checked={revokeLinks}
            onChange={(e) => setRevokeLinks(e.target.checked)}
          >
            {t("offboarding.revokeLinks", { count: p.activeLinks })}
          </Checkbox>
        </div>
      )}
      {p && recipientId && !report && (
        <Popconfirm
          title={t("offboarding.confirm", { name: p.fullName })}
          okButtonProps={{ danger: true }}
          onConfirm={() => run.mutate(undefined)}
        >
          <Button
            type="primary"
            danger
            loading={run.isPending}
            disabled={recipientId === userId}
          >
            {t("offboarding.run")}
          </Button>
        </Popconfirm>
      )}
      {report && <Report report={report} />}
    </Space>
  );
}

function UnitTab() {
  const { t } = useTranslation();
  const orgs = useOrgTree(false);
  const [userId, setUserId] = useState<string | null>(null);
  const [org, setOrg] = useState<string | null>(null);
  const [result, setResult] = useState<UnitChangeReport | null>(null);
  const change = useApiMutation(() => changeUserUnit(userId!, org), {
    invalidate: [["users"]],
    successMessage: t("offboarding.unitChanged"),
    onSuccess: setResult,
  });
  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <div>
        <Typography.Text strong>{t("offboarding.user")}</Typography.Text>
        <OneUser value={userId} onChange={setUserId} />
      </div>
      <div>
        <Typography.Text strong>{t("offboarding.newUnit")}</Typography.Text>
        <TreeSelect
          showSearch
          allowClear
          style={{ width: "100%" }}
          value={org ?? undefined}
          onChange={(v) => setOrg(v ?? null)}
          treeData={orgTreeToSelect(orgs.data)}
          filterTreeNode={filterTreeNodeUnaccent}
          treeDefaultExpandAll
          aria-label={t("offboarding.newUnit")}
        />
      </div>
      <Button
        type="primary"
        disabled={!userId}
        loading={change.isPending}
        onClick={() => change.mutate(undefined)}
      >
        {t("offboarding.changeUnit")}
      </Button>
      {result && (
        <Card
          size="small"
          title={t("offboarding.unitReport", {
            from: result.fromUnit,
            to: result.toUnit,
          })}
        >
          <Typography.Text strong>{t("offboarding.lost")}</Typography.Text>
          <List
            size="small"
            dataSource={result.lost}
            locale={{ emptyText: t("offboarding.nothing") }}
            renderItem={(x) => <List.Item>{x}</List.Item>}
          />
          <Typography.Text strong>{t("offboarding.gained")}</Typography.Text>
          <List
            size="small"
            dataSource={result.gained}
            locale={{ emptyText: t("offboarding.nothing") }}
            renderItem={(x) => <List.Item>{x}</List.Item>}
          />
        </Card>
      )}
    </Space>
  );
}

function HistoryTab() {
  const { t } = useTranslation();
  const list = useQuery({
    queryKey: ["offboardings"],
    queryFn: listOffboardings,
  });
  const [open, setOpen] = useState<OffboardingReport | null>(null);
  return (
    <>
      <Table<OffboardingReport>
        rowKey="id"
        loading={list.isLoading}
        dataSource={list.data ?? []}
        pagination={{ pageSize: 20 }}
        scroll={{ x: "max-content" }}
        locale={{
          emptyText: <Empty description={t("offboarding.noHistory")} />,
        }}
        onRow={(r) => ({
          onClick: () => setOpen(r),
          style: { cursor: "pointer" },
        })}
        columns={[
          { title: t("offboarding.user"), dataIndex: "userName" },
          { title: t("offboarding.recipient"), dataIndex: "recipientName" },
          {
            title: t("offboarding.performed"),
            key: "at",
            render: (_, r) =>
              `${r.performedByName} · ${formatDateTime(r.performedAt)}`,
          },
        ]}
      />
      {open && <Report report={open} />}
    </>
  );
}

/** Spec 8.3: the offboarding wizard (Drive to a recipient keeping shares, links, groups and Spaces, handover report) and unit changes. */
export default function OffboardingPage() {
  const { t } = useTranslation();
  return (
    <>
      <PageHeader
        title={t("menu.offboarding")}
        description={t("offboarding.description")}
      />
      <Tabs
        items={[
          {
            key: "offboard",
            label: t("offboarding.tabs.offboard"),
            children: <OffboardTab />,
          },
          {
            key: "unit",
            label: t("offboarding.tabs.unit"),
            children: <UnitTab />,
          },
          {
            key: "history",
            label: t("offboarding.tabs.history"),
            children: <HistoryTab />,
          },
        ]}
      />
    </>
  );
}
