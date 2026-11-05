import { useMemo, useState } from "react";
import {
  Alert,
  Button,
  Card,
  Divider,
  InputNumber,
  Popconfirm,
  Radio,
  Select,
  Space,
  Table,
  Tabs,
  Typography,
} from "antd";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  bulkDeleteInactiveGuests,
  bulkRemovePrincipal,
  bulkRevokeAnyoneLinks,
  exportAccessReport,
  runAccessReport,
  searchReportItems,
  type AccessReport,
  type AccessReportKind,
  type AccessReportParams,
} from "@/api/accessReports";
import { listAllWorkspaces } from "@/api/workspaces";
import ExportButtons from "@/components/ExportButtons";
import PageHeader from "@/components/PageHeader";
import { useMyWorkspaces } from "@/features/drive/hooks";
import { useApiMutation } from "@/hooks/useApiMutation";
import { useAllUserGroups } from "@/hooks/useLookups";
import { useCan } from "@/hooks/usePermissions";
import UserPicker from "@/pages/admin/userGroups/UserPicker";

const KINDS: AccessReportKind[] = [
  "check",
  "user-access",
  "external",
  "links",
  "unique",
  "expiring",
];

/** Spaces to report on: every one for administrators (searched), the ones I manage otherwise. */
function SpaceFilter({
  value,
  onChange,
  admin,
}: {
  value: string | null;
  onChange: (v: string | null) => void;
  admin: boolean;
}) {
  const { t } = useTranslation();
  const [q, setQ] = useState("");
  const all = useQuery({
    queryKey: ["report-spaces", q],
    queryFn: () =>
      listAllWorkspaces({ q: q || undefined, page: 1, pageSize: 30 }),
    enabled: admin,
  });
  const mine = useMyWorkspaces();
  const options = admin
    ? (all.data?.items ?? []).map((w) => ({
        value: w.id,
        label: w.type === "personal" ? `Drive: ${w.name}` : w.name,
      }))
    : (mine.data ?? [])
        .filter(
          (w) =>
            w.type !== "personal" &&
            (w.myRole === "owner" || w.myRole === "manager"),
        )
        .map((w) => ({ value: w.id, label: w.name }));
  return (
    <Select
      allowClear={admin}
      showSearch
      filterOption={admin ? false : undefined}
      optionFilterProp="label"
      onSearch={admin ? setQ : undefined}
      value={value ?? undefined}
      onChange={(v) => onChange(v ?? null)}
      options={options}
      placeholder={
        admin ? t("accessReports.allSpaces") : t("accessReports.pickSpace")
      }
      style={{ minWidth: 260 }}
      aria-label={t("accessReports.space")}
    />
  );
}

function ItemSearch({
  workspaceId,
  value,
  onChange,
}: {
  workspaceId: string | null;
  value: string | null;
  onChange: (v: string | null) => void;
}) {
  const { t } = useTranslation();
  const [q, setQ] = useState("");
  const items = useQuery({
    queryKey: ["report-items", workspaceId, q],
    queryFn: () => searchReportItems(workspaceId, q),
    enabled: q.length > 0,
  });
  return (
    <Select
      showSearch
      allowClear
      filterOption={false}
      onSearch={setQ}
      value={value ?? undefined}
      onChange={(v) => onChange(v ?? null)}
      loading={items.isFetching}
      options={(items.data ?? []).map((i) => ({
        value: i.id,
        label: `${i.name}${i.workspaceName ? ` — ${i.workspaceName}` : ""}`,
      }))}
      placeholder={t("accessReports.searchItem")}
      style={{ minWidth: 320 }}
      aria-label={t("accessReports.item")}
    />
  );
}

function Result({
  report,
  params,
}: {
  report: AccessReport;
  params: AccessReportParams;
}) {
  const { t } = useTranslation();
  const columns = useMemo(
    () =>
      report.columns.map((title, i) => ({
        title,
        key: String(i),
        render: (_: unknown, r: AccessReport["rows"][number]) =>
          r.cells[i] ?? "—",
      })),
    [report.columns],
  );
  return (
    <Card
      size="small"
      title={report.title}
      extra={
        <ExportButtons
          onExport={(format) => exportAccessReport(params, format)}
        />
      }
    >
      {report.summary && (
        <Alert
          type="info"
          showIcon
          message={report.summary}
          style={{ marginBottom: 8 }}
        />
      )}
      {report.truncated && (
        <Alert
          type="warning"
          showIcon
          message={t("accessReports.truncated")}
          style={{ marginBottom: 8 }}
        />
      )}
      <Table
        rowKey={(r, i) => `${r.itemId}-${i}`}
        size="small"
        dataSource={report.rows}
        columns={columns}
        scroll={{ x: "max-content" }}
        pagination={{ pageSize: 50 }}
      />
    </Card>
  );
}

function Reports({ admin }: { admin: boolean }) {
  const { t } = useTranslation();
  const groups = useAllUserGroups();
  const [kind, setKind] = useState<AccessReportKind>("check");
  const [workspaceId, setWorkspaceId] = useState<string | null>(() =>
    new URLSearchParams(window.location.search).get("workspaceId"),
  );
  const [who, setWho] = useState<"user" | "group">("user");
  const [userId, setUserId] = useState<string | null>(null);
  const [groupId, setGroupId] = useState<string | null>(null);
  const [itemId, setItemId] = useState<string | null>(null);
  const [days, setDays] = useState(30);
  const [ran, setRan] = useState<AccessReportParams | null>(null);
  const params: AccessReportParams = {
    kind,
    workspaceId,
    userId:
      kind === "user-access" || (kind === "check" && who === "user")
        ? userId
        : null,
    groupId: kind === "check" && who === "group" ? groupId : null,
    itemId: kind === "check" ? itemId : null,
    days: kind === "expiring" ? days : undefined,
  };
  const report = useQuery({
    queryKey: ["access-report", ran],
    queryFn: () => runAccessReport(ran!),
    enabled: !!ran,
  });
  const ready =
    (admin || !!workspaceId) &&
    (kind !== "check" ||
      (!!itemId && (who === "user" ? !!userId : !!groupId))) &&
    (kind !== "user-access" || !!userId);
  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Tabs
        activeKey={kind}
        onChange={(k) => {
          setKind(k as AccessReportKind);
          setRan(null);
        }}
        items={KINDS.map((k) => ({
          key: k,
          label: t(`accessReports.kinds.${k}`),
        }))}
      />
      <Typography.Paragraph type="secondary">
        {t(`accessReports.hints.${kind}`)}
      </Typography.Paragraph>
      <Space wrap align="start">
        <SpaceFilter
          value={workspaceId}
          onChange={setWorkspaceId}
          admin={admin}
        />
        {kind === "check" && (
          <>
            <ItemSearch
              workspaceId={workspaceId}
              value={itemId}
              onChange={setItemId}
            />
            <Radio.Group
              value={who}
              onChange={(e) => setWho(e.target.value)}
              optionType="button"
              options={[
                { value: "user", label: t("accessReports.user") },
                { value: "group", label: t("accessReports.group") },
              ]}
            />
          </>
        )}
        {(kind === "user-access" || (kind === "check" && who === "user")) && (
          <div style={{ minWidth: 320 }}>
            <UserPicker
              value={userId ? [userId] : []}
              onChange={(ids) => setUserId(ids[ids.length - 1] ?? null)}
            />
          </div>
        )}
        {kind === "check" && who === "group" && (
          <Select
            showSearch
            optionFilterProp="label"
            value={groupId ?? undefined}
            onChange={setGroupId}
            options={(groups.data ?? []).map((g) => ({
              value: g.id,
              label: g.name,
            }))}
            placeholder={t("accessReports.pickGroup")}
            style={{ minWidth: 240 }}
          />
        )}
        {kind === "expiring" && (
          <InputNumber
            min={1}
            max={365}
            value={days}
            onChange={(v) => setDays(v ?? 30)}
            addonAfter={t("accessReports.days")}
          />
        )}
        <Button
          type="primary"
          disabled={!ready}
          loading={report.isFetching}
          onClick={() => setRan({ ...params })}
        >
          {t("accessReports.run")}
        </Button>
      </Space>
      {report.data && ran && <Result report={report.data} params={ran} />}
    </Space>
  );
}

function Bulk() {
  const { t } = useTranslation();
  const [space, setSpace] = useState<string | null>(null);
  const [userId, setUserId] = useState<string | null>(null);
  const [days, setDays] = useState(90);
  const done = {
    successMessage: (r: { message: string }) => r.message,
    invalidate: [["access-report"]],
  };
  const revoke = useApiMutation(() => bulkRevokeAnyoneLinks(space), done);
  const remove = useApiMutation(() => bulkRemovePrincipal(userId!), done);
  const guests = useApiMutation(() => bulkDeleteInactiveGuests(days), done);
  return (
    <Space direction="vertical" style={{ width: "100%" }}>
      <Card size="small" title={t("accessReports.bulk.revokeTitle")}>
        <Space wrap>
          <SpaceFilter value={space} onChange={setSpace} admin />
          <Popconfirm
            title={
              space
                ? t("accessReports.bulk.revokeSpaceConfirm")
                : t("accessReports.bulk.revokeAllConfirm")
            }
            okButtonProps={{ danger: true }}
            onConfirm={() => revoke.mutate(undefined)}
          >
            <Button danger loading={revoke.isPending}>
              {t("accessReports.bulk.revoke")}
            </Button>
          </Popconfirm>
        </Space>
      </Card>
      <Card size="small" title={t("accessReports.bulk.removeTitle")}>
        <Space wrap>
          <div style={{ minWidth: 320 }}>
            <UserPicker
              value={userId ? [userId] : []}
              onChange={(ids) => setUserId(ids[ids.length - 1] ?? null)}
            />
          </div>
          <Popconfirm
            title={t("accessReports.bulk.removeConfirm")}
            okButtonProps={{ danger: true }}
            onConfirm={() => remove.mutate(undefined)}
            disabled={!userId}
          >
            <Button danger disabled={!userId} loading={remove.isPending}>
              {t("accessReports.bulk.remove")}
            </Button>
          </Popconfirm>
        </Space>
      </Card>
      <Card size="small" title={t("accessReports.bulk.guestsTitle")}>
        <Space wrap>
          <InputNumber
            min={1}
            max={3650}
            value={days}
            onChange={(v) => setDays(v ?? 90)}
            addonAfter={t("accessReports.days")}
          />
          <Popconfirm
            title={t("accessReports.bulk.guestsConfirm", { days })}
            okButtonProps={{ danger: true }}
            onConfirm={() => guests.mutate(undefined)}
          >
            <Button danger loading={guests.isPending}>
              {t("accessReports.bulk.guests")}
            </Button>
          </Popconfirm>
        </Space>
      </Card>
    </Space>
  );
}

/** Spec 8.4: access reports (administrators everywhere, Space managers in their Space) and bulk operations, exported to Excel / PDF. */
export default function AccessReportsPage() {
  const { t } = useTranslation();
  const can = useCan();
  const admin = can("accessreport.view");
  return (
    <>
      <PageHeader
        title={t("menu.accessReports")}
        description={t("accessReports.description")}
      />
      <Reports admin={admin} />
      {can("accessreport.admin") && (
        <>
          <Divider orientation="left">{t("accessReports.bulk.title")}</Divider>
          <Bulk />
        </>
      )}
    </>
  );
}
