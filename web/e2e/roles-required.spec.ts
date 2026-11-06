import { expect, test, type APIRequestContext } from '@playwright/test'

/**
 * An account without a role signs in but sees no documents, even in its groups: the user form requires a role, and the group's
 * member list warns about members whose role cannot see documents. Test data is created through the API and removed afterwards.
 */
const stamp = Date.now().toString().slice(-6)
let api: APIRequestContext
let admin: Record<string, string>
let roleId = ''
let userId = ''
let spaceId = ''

test.beforeAll(async ({ playwright, baseURL }) => {
  api = await playwright.request.newContext({ baseURL })
  const login = await (await api.post('/api/v1/auth/login', {
    data: { username: process.env.E2E_USERNAME ?? 'admin', password: process.env.E2E_PASSWORD ?? 'Admin@Smoke2026' },
  })).json()
  admin = { Authorization: `Bearer ${login.accessToken}` }
  // A role granting nothing: the account has a role, but it cannot see documents.
  roleId = (await (await api.post('/api/v1/roles', { headers: admin, data: { code: `e2e_rong_${stamp}`, name: `E2E vai trò trống ${stamp}` } })).json()).id
  const created = await api.post('/api/v1/users', {
    headers: admin,
    data: { username: `e2eblind${stamp}`, fullName: `Người Chưa Xem ${stamp}`, isActive: true, password: 'Khoi@Tao12345', roleIds: [roleId] },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  userId = (await created.json()).id
  const space = await api.post('/api/v1/workspaces', { headers: admin, data: { name: `E2E Nhóm ${stamp}`, type: 'group' } })
  expect(space.ok(), await space.text()).toBeTruthy()
  spaceId = (await space.json()).id
})

test.afterAll(async () => {
  if (spaceId) {
    await api.put(`/api/v1/workspaces/${spaceId}/status`, { headers: admin, data: { status: 'deleted' } })
    await api.delete(`/api/v1/workspaces/trash/${spaceId}`, { headers: admin })
  }
  if (userId) await api.delete(`/api/v1/users/${userId}`, { headers: admin })
  if (roleId) await api.delete(`/api/v1/roles/${roleId}`, { headers: admin })
  await api.dispose()
})

test('tạo người dùng phải chọn vai trò', async ({ page }) => {
  await page.goto('/admin/users')
  await page.getByRole('main').getByRole('button', { name: /Thêm/ }).first().click()
  const drawer = page.locator('.ant-drawer-content')
  await drawer.getByLabel('Tên đăng nhập').fill(`e2ekhongvaitro${stamp}`)
  await drawer.getByLabel('Họ và tên').fill('Không Vai Trò')
  await drawer.getByRole('button', { name: 'Lưu' }).click()
  await expect(drawer.getByText(/Vui lòng chọn ít nhất một vai trò/)).toBeVisible()
  // Nothing was created.
  const found = await (await api.get(`/api/v1/users?q=e2ekhongvaitro${stamp}`, { headers: admin })).json()
  expect(found.total).toBe(0)
})

test('thêm vào nhóm người không xem được tài liệu thì cảnh báo', async ({ page }) => {
  await page.goto(`/workspaces/${spaceId}`)
  await page.getByRole('button', { name: /Thành viên/ }).first().click()
  const drawer = page.locator('.ant-drawer-content')
  await drawer.locator('.ant-select').first().click()
  await page.keyboard.type(`Người Chưa Xem ${stamp}`)
  await page.locator('.ant-select-dropdown:visible').getByText(`Người Chưa Xem ${stamp}`).first().click()
  await page.keyboard.press('Escape')
  await drawer.getByRole('button', { name: /Thêm/ }).click()
  await expect(page.locator('.ant-modal-confirm-title').filter({ hasText: 'Thành viên chưa xem được tư liệu' })).toBeVisible()
  await page.getByRole('button', { name: /OK|Đồng ý|Đã hiểu/ }).last().click()
  // The member list keeps flagging them.
  const row = drawer.getByRole('row').filter({ hasText: `Người Chưa Xem ${stamp}` })
  await expect(row.getByText('Chưa có vai trò')).toBeVisible()
  await expect(drawer.getByText(/1 thành viên chưa có vai trò được xem tài liệu/)).toBeVisible()
})

test('tài khoản Chỉ xem toàn quyền trong Tài liệu của tôi (như Google Drive)', async ({ browser }) => {
  const roles: { id: string; code: string }[] = await (await api.get('/api/v1/roles', { headers: admin })).json()
  const viewerRole = roles.find((r) => r.code === 'chi_xem')!.id
  const username = `e2echixem${stamp}`
  const created = await api.post('/api/v1/users', {
    headers: admin,
    data: { username, fullName: `Chỉ Xem ${stamp}`, isActive: true, password: 'Khoi@Tao12345', roleIds: [viewerRole] },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const id = (await created.json()).id
  try {
    const first = await (await api.post('/api/v1/auth/login', { data: { username, password: 'Khoi@Tao12345' } })).json()
    await api.post('/api/v1/auth/change-password', {
      headers: { Authorization: `Bearer ${first.accessToken}` },
      data: { currentPassword: 'Khoi@Tao12345', newPassword: 'Chi@Xem2026x' },
    })
    const ctx = await browser.newContext({ storageState: { cookies: [], origins: [] } })
    const page = await ctx.newPage()
    await page.goto('/login')
    await page.getByLabel(/tên đăng nhập/i).fill(username)
    await page.getByLabel(/mật khẩu/i).fill('Chi@Xem2026x')
    await page.getByRole('button', { name: /đăng nhập/i }).click()
    await page.waitForURL((u) => !u.pathname.startsWith('/login'))
    await page.goto('/my-drive')
    await page.waitForLoadState('networkidle')
    await page.getByRole('main').getByRole('button', { name: /Mới$/ }).first().click()
    await page.getByRole('menuitem', { name: /Thư mục/ }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(`Của Chỉ Xem ${stamp}`)
    await page.getByRole('dialog').getByRole('button', { name: /Tạo|Đồng ý|OK/ }).click()
    await expect(page.getByRole('row', { name: new RegExp(`Của Chỉ Xem ${stamp}`) })).toBeVisible()
    await ctx.close()
  } finally {
    await api.delete(`/api/v1/users/${id}`, { headers: admin })
  }
})
