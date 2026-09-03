import { useEffect, useSyncExternalStore } from 'react';
import { useThemePreference } from '../stores/preferencesStore';

const darkQuery = () => window.matchMedia('(prefers-color-scheme: dark)');

function subscribeToSystemTheme(onChange: () => void) {
    const media = darkQuery();
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
}

export function useSystemPrefersDark(): boolean {
    return useSyncExternalStore(
        subscribeToSystemTheme,
        () => darkQuery().matches,
        () => false,
    );
}

/** The theme actually in effect, combining the stored preference with the OS setting. */
export function useResolvedTheme(): 'light' | 'dark' {
    const preference = useThemePreference();
    const systemDark = useSystemPrefersDark();
    return preference === 'dark' || (preference === 'system' && systemDark) ? 'dark' : 'light';
}

export function useApplyTheme() {
    const resolved = useResolvedTheme();
    useEffect(() => {
        document.documentElement.classList.toggle('dark', resolved === 'dark');
        document.documentElement.style.colorScheme = resolved;
    }, [resolved]);
    return resolved;
}
