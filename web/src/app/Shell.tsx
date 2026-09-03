import { useIsFetching, useIsMutating } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import { usePermissions } from '../auth/permissions';
import { preferencesActions, useSidebarCollapsed, useThemePreference } from '../stores/preferencesStore';
import { Badge, IconButton, cx } from '../ui';
import { Icons } from '../ui/icons';
import { matchRoute } from './routeMatch';
import { Link, useLocationPath } from './router';
import { isNavActive, navPath, navRoutes, routes } from './routes';
import { useResolvedTheme } from './theme';

export function Shell() {
    const pathname = useLocationPath();
    const match = matchRoute(pathname, routes);
    const collapsed = useSidebarCollapsed();
    const fetching = useIsFetching();
    const mutating = useIsMutating();
    const busy = fetching + mutating > 0;

    return (
        <div className="flex min-h-screen">
            <div
                aria-hidden="true"
                className={cx('dp-topbar-progress transition-opacity duration-300', busy ? 'opacity-100' : 'opacity-0')}
            >
                <div className="brand-gradient dp-indeterminate h-full w-1/3" />
            </div>
            <Sidebar collapsed={collapsed} pathname={pathname} />
            <main
                className={cx(
                    'min-w-0 flex-1 transition-[padding] duration-200',
                    collapsed ? 'md:pl-[68px]' : 'md:pl-[248px]',
                )}
            >
                <MobileBar pathname={pathname} />
                <div className="mx-auto w-full max-w-[1320px] px-5 py-6 md:px-8 md:py-8" key={pathname}>
                    {match ? (
                        <div className="dp-fade-up">{match.route.render(match.params)}</div>
                    ) : (
                        <NotFound pathname={pathname} />
                    )}
                </div>
            </main>
        </div>
    );
}

function Sidebar({ collapsed, pathname }: Readonly<{ collapsed: boolean; pathname: string }>) {
    const { principal, signOut } = useAuth();
    const permissions = usePermissions();
    const theme = useThemePreference();
    const isDark = useResolvedTheme() === 'dark';

    return (
        <aside
            className={cx(
                'fixed inset-y-0 left-0 z-40 hidden flex-col border-r border-border bg-surface md:flex',
                collapsed ? 'w-[68px]' : 'w-[248px]',
                'transition-[width] duration-200',
            )}
        >
            <div
                className={cx(
                    'flex h-16 items-center gap-3 border-b border-border',
                    collapsed ? 'justify-center px-0' : 'px-5',
                )}
            >
                <Link className="flex items-center gap-3" to="/">
                    <span className="brand-gradient flex size-9 shrink-0 items-center justify-center rounded-xl text-white shadow-sm">
                        <Icons.Rocket size={18} />
                    </span>
                    {!collapsed ? (
                        <span className="text-[17px] font-bold tracking-tight text-fg">DataPitcher</span>
                    ) : null}
                </Link>
            </div>

            <nav aria-label="Application" className="flex-1 space-y-1 px-3 py-4">
                {navRoutes.map((route) => {
                    const Icon = Icons[route.nav!.icon];
                    const active = isNavActive(route, pathname);
                    return (
                        <Link
                            aria-current={active ? 'page' : undefined}
                            className={cx(
                                'flex h-10 items-center gap-3 rounded-xl px-3 text-sm font-medium transition-colors',
                                active
                                    ? 'bg-accent-soft text-accent'
                                    : 'text-fg-muted hover:bg-surface-2 hover:text-fg',
                                collapsed && 'justify-center px-0',
                            )}
                            key={route.path}
                            title={route.nav!.label}
                            to={navPath(route)}
                        >
                            <Icon size={18} />
                            {!collapsed ? route.nav!.label : null}
                        </Link>
                    );
                })}
            </nav>

            <div className="space-y-2 border-t border-border p-3">
                <div className={cx('flex items-center gap-1', collapsed ? 'flex-col' : 'justify-between')}>
                    <IconButton
                        label={isDark ? 'Switch to light theme' : 'Switch to dark theme'}
                        onClick={() => preferencesActions.setTheme(isDark ? 'light' : 'dark')}
                        size="sm"
                    >
                        {isDark ? <Icons.Sun size={16} /> : <Icons.Moon size={16} />}
                    </IconButton>
                    {!collapsed && theme === 'system' ? (
                        <span className="text-[11px] text-fg-faint">System theme</span>
                    ) : null}
                    <IconButton
                        label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
                        onClick={preferencesActions.toggleSidebar}
                        size="sm"
                    >
                        {collapsed ? <Icons.ChevronRight size={16} /> : <Icons.ArrowLeft size={16} />}
                    </IconButton>
                </div>
                {collapsed ? (
                    <div className="flex flex-col items-center gap-1">
                        <span
                            className="flex size-8 items-center justify-center rounded-full bg-accent text-xs font-bold text-accent-fg uppercase"
                            title={`${principal.subjectId} · ${principal.roles.join(', ') || 'no roles'}`}
                        >
                            {principal.subjectId.slice(0, 2)}
                        </span>
                        <IconButton label="Sign out" onClick={signOut} size="sm">
                            <Icons.Logout size={16} />
                        </IconButton>
                    </div>
                ) : (
                    <div className="flex items-center gap-3 rounded-xl bg-surface-2 p-2.5">
                        <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent text-xs font-bold text-accent-fg uppercase">
                            {principal.subjectId.slice(0, 2)}
                        </span>
                        <div className="min-w-0 flex-1">
                            <div className="truncate text-[13px] font-semibold text-fg">{principal.subjectId}</div>
                            <div className="mt-0.5 flex flex-wrap gap-1">
                                {principal.roles.length === 0 ? (
                                    <span className="text-[11px] text-fg-faint">No roles</span>
                                ) : (
                                    principal.roles.map((role) => (
                                        <Badge className="!h-4.5 !px-1.5 !text-[10px]" key={role} tone="accent">
                                            {role}
                                        </Badge>
                                    ))
                                )}
                            </div>
                        </div>
                        <IconButton className="shrink-0" label="Sign out" onClick={signOut} size="sm">
                            <Icons.Logout size={16} />
                        </IconButton>
                    </div>
                )}
                {!collapsed ? (
                    <p className="px-1 text-[11px] text-fg-faint">
                        Permissions{' '}
                        {permissions.source === 'server'
                            ? 'verified by the API'
                            : permissions.source === 'roles'
                              ? 'derived from token roles'
                              : 'pending'}
                    </p>
                ) : null}
            </div>
        </aside>
    );
}

function MobileBar({ pathname }: Readonly<{ pathname: string }>) {
    const { signOut } = useAuth();
    return (
        <div className="sticky top-0 z-30 border-b border-border bg-surface/90 backdrop-blur md:hidden">
            <div className="flex h-14 items-center justify-between px-4">
                <Link className="flex items-center gap-2" to="/">
                    <span className="brand-gradient flex size-8 items-center justify-center rounded-lg text-white">
                        <Icons.Rocket size={16} />
                    </span>
                    <span className="text-[15px] font-bold text-fg">DataPitcher</span>
                </Link>
                <IconButton label="Sign out" onClick={signOut} size="sm">
                    <Icons.Logout size={16} />
                </IconButton>
            </div>
            <nav aria-label="Application" className="scrollbar-thin flex gap-1 overflow-x-auto px-3 pb-2">
                {navRoutes.map((route) => {
                    const Icon = Icons[route.nav!.icon];
                    const active = isNavActive(route, pathname);
                    return (
                        <Link
                            aria-current={active ? 'page' : undefined}
                            className={cx(
                                'flex h-8 shrink-0 items-center gap-1.5 rounded-lg px-2.5 text-[13px] font-medium',
                                active ? 'bg-accent-soft text-accent' : 'text-fg-muted',
                            )}
                            key={route.path}
                            to={navPath(route)}
                        >
                            <Icon size={15} />
                            {route.nav!.label}
                        </Link>
                    );
                })}
            </nav>
        </div>
    );
}

function NotFound({ pathname }: Readonly<{ pathname: string }>) {
    return (
        <div className="py-20 text-center">
            <div className="text-6xl font-black text-gradient">404</div>
            <p className="mt-2 text-fg-muted">
                Nothing lives at <code className="font-mono">{pathname}</code>.
            </p>
            <Link className="mt-6 inline-block text-accent underline" to="/">
                Back to overview
            </Link>
        </div>
    );
}
