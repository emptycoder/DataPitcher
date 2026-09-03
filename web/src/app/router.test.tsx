import { afterEach, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Link, navigate, useLocationPath } from './router';

function LocationProbe() {
  const pathname = useLocationPath();
  return <output>{pathname}</output>;
}

afterEach(() => {
  cleanup();
  window.history.replaceState(null, '', '/');
});

it('reflects the current path and updates on programmatic navigation', () => {
  render(<LocationProbe />);
  expect(screen.getByRole('status')).toHaveTextContent('/');
  act(() => navigate('/connections'));
  expect(screen.getByRole('status')).toHaveTextContent('/connections');
});

it('reflects browser back and forward navigation', () => {
  render(<LocationProbe />);
  act(() => navigate('/connections'));
  act(() => {
    window.history.pushState(null, '', '/plan-review');
    fireEvent.popState(window);
  });
  expect(screen.getByRole('status')).toHaveTextContent('/plan-review');
});

it('navigates on a plain left click without reloading the page', () => {
  render(<><LocationProbe /><Link to="/transfer-monitor">Transfer monitor</Link></>);
  fireEvent.click(screen.getByRole('link', { name: 'Transfer monitor' }));
  expect(screen.getByRole('status')).toHaveTextContent('/transfer-monitor');
  expect(window.location.pathname).toBe('/transfer-monitor');
});

it('lets a modified click fall through to default browser navigation', () => {
  render(<><LocationProbe /><Link to="/transfer-monitor">Transfer monitor</Link></>);
  const link = screen.getByRole('link', { name: 'Transfer monitor' });
  fireEvent.click(link, { metaKey: true });
  fireEvent.click(link, { ctrlKey: true });
  fireEvent.click(link, { shiftKey: true });
  fireEvent.click(link, { altKey: true });
  fireEvent.click(link, { button: 1 });
  expect(screen.getByRole('status')).toHaveTextContent('/');
});
