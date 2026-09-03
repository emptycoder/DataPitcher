import { useSyncExternalStore, type MouseEvent, type ReactNode } from 'react';

const NAVIGATE_EVENT = 'datapitcher:navigate';

export function navigate(path: string) {
  window.history.pushState(null, '', path);
  window.dispatchEvent(new Event(NAVIGATE_EVENT));
}

function subscribe(onChange: () => void) {
  window.addEventListener('popstate', onChange);
  window.addEventListener(NAVIGATE_EVENT, onChange);
  return () => {
    window.removeEventListener('popstate', onChange);
    window.removeEventListener(NAVIGATE_EVENT, onChange);
  };
}

function getPathname() {
  return window.location.pathname;
}

export function useLocationPath(): string {
  return useSyncExternalStore(subscribe, getPathname);
}

export function Link({ to, children }: Readonly<{ to: string; children: ReactNode }>) {
  function onClick(event: MouseEvent<HTMLAnchorElement>) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    navigate(to);
  }
  return <a href={to} onClick={onClick}>{children}</a>;
}
