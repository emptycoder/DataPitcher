import { useSyncExternalStore, type ComponentPropsWithoutRef, type MouseEvent } from 'react';

const NAVIGATE_EVENT = 'datapitcher:navigate';

export function navigate(path: string, options: Readonly<{ replace?: boolean }> = {}) {
  if (options.replace) window.history.replaceState(null, '', path);
  else window.history.pushState(null, '', path);
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

function getSearch() {
  return window.location.search;
}

export function useLocationPath(): string {
  return useSyncExternalStore(subscribe, getPathname);
}

export function useLocationSearch(): URLSearchParams {
  const search = useSyncExternalStore(subscribe, getSearch);
  return new URLSearchParams(search);
}

export type LinkProps = Omit<ComponentPropsWithoutRef<'a'>, 'href'> & Readonly<{ to: string }>;

export function Link({ to, onClick: onClickProp, children, ...props }: LinkProps) {
  function onClick(event: MouseEvent<HTMLAnchorElement>) {
    onClickProp?.(event);
    if (event.defaultPrevented) return;
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    navigate(to);
  }
  return (
    <a {...props} href={to} onClick={onClick}>
      {children}
    </a>
  );
}
