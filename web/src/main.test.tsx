import { afterEach, beforeEach, expect, it, vi } from 'vitest';

const render = vi.fn();
vi.mock('react-dom/client', () => ({ createRoot: vi.fn(() => ({ render })) }));

beforeEach(() => {
  document.body.innerHTML = '<div id="root"></div>';
});
afterEach(() => {
  vi.clearAllMocks();
  vi.resetModules();
});

it('mounts the application into the Vite root', async () => {
  await import('./main');
  expect(render).toHaveBeenCalledOnce();
});
