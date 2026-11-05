import {
  expect,
  test,
  type APIRequestContext,
  type Browser,
  type Page,
} from "@playwright/test";

/**
 * Spec 13.8 permission flows, end to end: request access then approval; breaking inheritance takes access away; an "Anyone" link opens
 * without signing in; "Chỉ xem" shows a watermark and no download; offboarding hands a Drive over with a report.
 * Users and files are created through the API and removed afterwards.
 */
const stamp = Date.now().toString().slice(-6);
const PW1 = "Khoi@Tao12345";
const PW2 = "Quyen@E2E2026";
const PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  "base64",
);
interface TestUser {
  id: string;
  username: string;
  fullName: string;
  token: string;
  ws: string;
}
let api: APIRequestContext;
let admin: Record<string, string>;
let levels: Record<string, string> = {};
const users: TestUser[] = [];
const bearer = (u: TestUser) => ({ Authorization: `Bearer ${u.token}` });

async function createUser(
  username: string,
  fullName: string,
  roleId: string,
): Promise<TestUser> {
  const created = await api.post("/api/v1/users", {
    headers: admin,
    data: {
      username,
      fullName,
      isActive: true,
      password: PW1,
      roleIds: [roleId],
    },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  const { accessToken } = await (
    await api.post("/api/v1/auth/login", { data: { username, password: PW1 } })
  ).json();
  const changed = await api.post("/api/v1/auth/change-password", {
    headers: { Authorization: `Bearer ${accessToken}` },
    data: { currentPassword: PW1, newPassword: PW2 },
  });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  const token = (await changed.json()).accessToken;
  const me = await (
    await api.get("/api/v1/auth/me", {
      headers: { Authorization: `Bearer ${token}` },
    })
  ).json();
  const u = {
    id: me.id,
    username,
    fullName,
    token,
    ws: me.personalWorkspaceId,
  };
  users.push(u);
  return u;
}

async function upload(
  u: TestUser,
  name: string,
  buffer: Buffer,
  mimeType: string,
  folderId?: string,
): Promise<string> {
  const multipart: Record<
    string,
    string | { name: string; mimeType: string; buffer: Buffer }
  > = { file: { name, mimeType, buffer }, workspaceId: u.ws };
  if (folderId) multipart.folderId = folderId;
  const r = await api.post("/api/v1/files/upload", {
    headers: bearer(u),
    multipart,
  });
  expect(r.ok(), await r.text()).toBeTruthy();
  return (await r.json()).fileId;
}

async function grant(
  owner: TestUser,
  itemId: string,
  to: TestUser,
  level: string,
) {
  const r = await api.post(`/api/v1/items/${itemId}/permissions`, {
    headers: bearer(owner),
    data: {
      targets: [{ kind: "user", id: to.id }],
      levelId: levels[level],
      notify: false,
    },
  });
  expect(r.ok(), await r.text()).toBeTruthy();
}

async function loginPage(browser: Browser, u: TestUser): Promise<Page> {
  const ctx = await browser.newContext({
    storageState: { cookies: [], origins: [] },
  });
  const page = await ctx.newPage();
  await page.goto("/login");
  await page.getByLabel(/tên đăng nhập/i).fill(u.username);
  await page.getByLabel(/mật khẩu/i).fill(PW2);
  await page.getByRole("button", { name: /đăng nhập/i }).click();
  await expect(page).toHaveURL(/\/$/, { timeout: 20_000 });
  return page;
}

let owner: TestUser;
let other: TestUser;
let editorRole = "";

test.beforeAll(async ({ playwright, baseURL }) => {
  api = await playwright.request.newContext({ baseURL });
  const login = await (
    await api.post("/api/v1/auth/login", {
      data: {
        username: process.env.E2E_USERNAME ?? "admin",
        password: process.env.E2E_PASSWORD ?? "Admin@Smoke2026",
      },
    })
  ).json();
  admin = { Authorization: `Bearer ${login.accessToken}` };
  const roles: { id: string; code: string }[] = await (
    await api.get("/api/v1/roles", { headers: admin })
  ).json();
  editorRole = roles.find((r) => r.code === "bien_tap")!.id;
  owner = await createUser(`e2epa${stamp}`, `Chủ Tư Liệu ${stamp}`, editorRole);
  other = await createUser(`e2epb${stamp}`, `Người Xin ${stamp}`, editorRole);
  const all: { id: string; code: string }[] = await (
    await api.get("/api/v1/permission-levels", { headers: bearer(owner) })
  ).json();
  levels = Object.fromEntries(all.map((l) => [l.code, l.id]));
});

test.afterAll(async () => {
  for (const u of users) {
    const items =
      (
        await (
          await api.get(`/api/v1/workspaces/${u.ws}/items?pageSize=100`, {
            headers: bearer(u),
          })
        ).json()
      ).items ?? [];
    const refs = items.map((i: { type: string; id: string }) => ({
      type: i.type,
      id: i.id,
    }));
    if (refs.length) {
      await api.post("/api/v1/items/delete", {
        headers: bearer(u),
        data: { items: refs },
      });
      for (let i = 0; i < 2; i++)
        await api.post("/api/v1/trash/purge", {
          headers: bearer(u),
          data: refs,
        });
    }
    await api.delete(`/api/v1/users/${u.id}`, { headers: admin });
  }
  await api.dispose();
});

test("yêu cầu quyền truy cập rồi được duyệt", async ({ browser }) => {
  test.setTimeout(120_000);
  const name = `xin-quyen-${stamp}.txt`;
  const file = await upload(
    owner,
    name,
    Buffer.from("Nội dung cần xin quyền"),
    "text/plain",
  );
  const asker = await loginPage(browser, other);
  await asker.goto(`/request-access/${file}`);
  await expect(asker.getByText("Bạn cần quyền truy cập")).toBeVisible();
  await asker.getByRole("button", { name: "Gửi yêu cầu" }).click();
  await expect(
    asker.getByText(/đang chờ duyệt|Đã gửi yêu cầu/).first(),
  ).toBeVisible();

  const decider = await loginPage(browser, owner);
  await decider.goto("/access-requests");
  const row = decider.getByRole("row", { name: new RegExp(name) });
  await expect(row).toBeVisible({ timeout: 15_000 });
  await row.getByRole("button", { name: "Duyệt" }).click();
  await Promise.all([
    decider.waitForResponse((r) => r.url().includes("/decide") && r.ok()),
    decider.getByRole("dialog").getByRole("button", { name: "Duyệt" }).click(),
  ]);
  await asker.goto(`/files/${file}`);
  await expect(asker.getByText(name).first()).toBeVisible({ timeout: 15_000 });
  await asker.context().close();
  await decider.context().close();
});

test("ngắt kế thừa làm mất quyền được chia sẻ từ thư mục", async ({
  browser,
}) => {
  test.setTimeout(120_000);
  const folder = (
    await (
      await api.post("/api/v1/folders", {
        headers: bearer(owner),
        data: { workspaceId: owner.ws, name: `Kế thừa ${stamp}` },
      })
    ).json()
  ).id;
  const name = `trong-thu-muc-${stamp}.txt`;
  const file = await upload(
    owner,
    name,
    Buffer.from("Kế thừa từ thư mục"),
    "text/plain",
    folder,
  );
  await grant(owner, folder, other, "viewer");
  const reader = await loginPage(browser, other);
  await reader.goto(`/files/${file}`);
  await expect(reader.getByText(name).first()).toBeVisible({ timeout: 15_000 });
  // The file stops inheriting without copying: the folder's share no longer reaches it, on every page.
  const broke = await api.post(`/api/v1/items/${file}/inheritance?copy=false`, {
    headers: bearer(owner),
  });
  expect(broke.ok(), await broke.text()).toBeTruthy();
  await reader.goto(`/files/${file}`);
  await expect(
    reader
      .getByText(/Bạn cần quyền truy cập|không tồn tại|Không tìm thấy/i)
      .first(),
  ).toBeVisible({ timeout: 15_000 });
  expect(
    (
      await api.get(`/api/v1/files/${file}`, { headers: bearer(other) })
    ).status(),
  ).toBe(404);
  const search = await (
    await api.get(`/api/v1/search?q=${encodeURIComponent(name)}`, {
      headers: bearer(other),
    })
  ).json();
  expect(search.total).toBe(0);
  await reader.context().close();
});

test('liên kết "Bất kỳ ai" mở được không cần đăng nhập', async ({
  browser,
}) => {
  const name = `lien-ket-${stamp}.png`;
  const file = await upload(owner, name, PNG, "image/png");
  const link = await api.post("/api/v1/share-links", {
    headers: bearer(owner),
    data: {
      resourceType: "file",
      resourceId: file,
      scope: "anyone",
      levelId: levels.viewer,
      allowDownload: true,
    },
  });
  expect(link.ok(), await link.text()).toBeTruthy();
  const ctx = await browser.newContext({
    storageState: { cookies: [], origins: [] },
  });
  const page = await ctx.newPage();
  await page.goto(`/s/${(await link.json()).token}`);
  await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });
  await ctx.close();
});

test('người "Chỉ xem" thấy hình mờ, không có nút tải xuống', async ({
  browser,
}) => {
  const name = `chi-xem-${stamp}.png`;
  const file = await upload(owner, name, PNG, "image/png");
  await grant(owner, file, other, "view_only");
  const page = await loginPage(browser, other);
  await page.goto(`/files/${file}`);
  await expect(page.getByText(name).first()).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".bd-watermark")).toBeVisible();
  await expect(page.getByRole("button", { name: /Tải xuống/ })).toHaveCount(0);
  await page.context().close();
});

test("nghỉ việc: bàn giao Drive cho người khác và lập biên bản", async ({
  page,
}) => {
  test.setTimeout(120_000);
  const leaver = await createUser(
    `e2epc${stamp}`,
    `Người Nghỉ ${stamp}`,
    editorRole,
  );
  await upload(
    leaver,
    `ban-giao-${stamp}.txt`,
    Buffer.from("Bàn giao"),
    "text/plain",
  );
  await page.goto("/admin/offboarding");
  const pick = async (index: number, who: TestUser) => {
    await page.locator(".ant-select").nth(index).click();
    await page.keyboard.type(who.username);
    await page
      .locator(".ant-select-dropdown:visible")
      .getByText(new RegExp(who.username))
      .first()
      .click();
    await page.keyboard.press("Escape");
  };
  await pick(0, leaver);
  await expect(
    page.getByText(`Dữ liệu và quyền của ${leaver.fullName}`),
  ).toBeVisible({ timeout: 15_000 });
  await pick(1, owner);
  await page.getByRole("button", { name: "Bàn giao và vô hiệu hóa" }).click();
  await Promise.all([
    page.waitForResponse(
      (r) =>
        r.url().includes(`/admin/offboarding/${leaver.id}`) &&
        r.request().method() === "POST" &&
        r.ok(),
    ),
    page
      .locator(".ant-popconfirm")
      .getByRole("button", { name: /OK|Đồng ý/ })
      .click(),
  ]);
  await expect(
    page.getByText(`Biên bản bàn giao — ${leaver.fullName}`),
  ).toBeVisible();
  const items = (
    await (
      await api.get(`/api/v1/workspaces/${owner.ws}/items?pageSize=100`, {
        headers: bearer(owner),
      })
    ).json()
  ).items;
  expect(
    items.some(
      (i: { name: string }) => i.name === `Dữ liệu từ ${leaver.fullName}`,
    ),
  ).toBeTruthy();
});
