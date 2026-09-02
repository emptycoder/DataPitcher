import { expect, test } from 'playwright/test';

const topology = {
  revision: 'r1',
  plannedTableIds: ['orders'],
  tables: [
    { id: 'orders', schema: 'sales', name: 'orders', componentId: 'orders', state: 'root-selected' },
    { id: 'customers', schema: 'sales', name: 'customers', componentId: 'customers', state: 'required-dependency' },
  ],
  relationships: [{ id: 'orders-customers', name: 'FK_orders_customers', childTableId: 'orders', parentTableId: 'customers' }],
};

test('runs the production ELK worker for the graph route', async ({ page }) => {
  await page.route('/api/plans/plan-1/schema-dependency-graph', (route) => route.fulfill({ json: topology }));
  await page.goto('/dependency-graph/plan-1');

  await expect(page.getByText('orders depends on customers')).toBeVisible();
  await expect.poll(async () => new Set(await page.locator('.react-flow__node').evaluateAll((nodes) => nodes.map((node) => node.getAttribute('style')))).size).toBe(2);
});
