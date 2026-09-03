import type { SVGProps } from 'react';

type IconProps = SVGProps<SVGSVGElement> & Readonly<{ size?: number }>;

function Svg({ size = 18, children, ...props }: IconProps) {
    return (
        <svg
            aria-hidden="true"
            fill="none"
            height={size}
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            viewBox="0 0 24 24"
            width={size}
            {...props}
        >
            {children}
        </svg>
    );
}

export const Icons = {
    Home: (p: IconProps) => (
        <Svg {...p}>
            <path d="M3 11l9-8 9 8v9a2 2 0 0 1-2 2h-4v-7H9v7H5a2 2 0 0 1-2-2z" />
        </Svg>
    ),
    Plug: (p: IconProps) => (
        <Svg {...p}>
            <path d="M9 2v6M15 2v6M5 8h14v3a7 7 0 0 1-14 0zM12 18v4" />
        </Svg>
    ),
    Database: (p: IconProps) => (
        <Svg {...p}>
            <ellipse cx="12" cy="5" rx="9" ry="3" />
            <path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3" />
        </Svg>
    ),
    Schema: (p: IconProps) => (
        <Svg {...p}>
            <rect x="3" y="3" width="7" height="7" rx="1.5" />
            <rect x="14" y="3" width="7" height="7" rx="1.5" />
            <rect x="8.5" y="14" width="7" height="7" rx="1.5" />
            <path d="M6.5 10v2.5h11V10M12 12.5V14" />
        </Svg>
    ),
    Filter: (p: IconProps) => (
        <Svg {...p}>
            <path d="M3 5h18l-7 8v6l-4 2v-8z" />
        </Svg>
    ),
    Clipboard: (p: IconProps) => (
        <Svg {...p}>
            <rect x="5" y="4" width="14" height="17" rx="2" />
            <path d="M9 4V3h6v1M9 11h6M9 15h6" />
        </Svg>
    ),
    Rocket: (p: IconProps) => (
        <Svg {...p}>
            <path d="M5 15c-1 2-1 4-1 4s2 0 4-1M12 15l-3-3c1-5 4-8 10-9-1 6-4 9-9 10zM9 12l-3 0 2-3M12 15l0 3-3 2" />
            <circle cx="15" cy="9" r="1" />
        </Svg>
    ),
    Play: (p: IconProps) => (
        <Svg {...p}>
            <path d="M7 4l12 8-12 8z" />
        </Svg>
    ),
    Pause: (p: IconProps) => (
        <Svg {...p}>
            <path d="M8 5v14M16 5v14" />
        </Svg>
    ),
    Stop: (p: IconProps) => (
        <Svg {...p}>
            <rect x="6" y="6" width="12" height="12" rx="2" />
        </Svg>
    ),
    Check: (p: IconProps) => (
        <Svg {...p}>
            <path d="M5 12l5 5L20 7" />
        </Svg>
    ),
    X: (p: IconProps) => (
        <Svg {...p}>
            <path d="M6 6l12 12M18 6L6 18" />
        </Svg>
    ),
    Plus: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 5v14M5 12h14" />
        </Svg>
    ),
    Refresh: (p: IconProps) => (
        <Svg {...p}>
            <path d="M21 12a9 9 0 1 1-2.6-6.4M21 3v6h-6" />
        </Svg>
    ),
    Search: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="11" cy="11" r="7" />
            <path d="M20 20l-3.5-3.5" />
        </Svg>
    ),
    ArrowRight: (p: IconProps) => (
        <Svg {...p}>
            <path d="M5 12h14M13 6l6 6-6 6" />
        </Svg>
    ),
    ArrowLeft: (p: IconProps) => (
        <Svg {...p}>
            <path d="M19 12H5M11 6l-6 6 6 6" />
        </Svg>
    ),
    ChevronDown: (p: IconProps) => (
        <Svg {...p}>
            <path d="M6 9l6 6 6-6" />
        </Svg>
    ),
    ChevronRight: (p: IconProps) => (
        <Svg {...p}>
            <path d="M9 6l6 6-6 6" />
        </Svg>
    ),
    Copy: (p: IconProps) => (
        <Svg {...p}>
            <rect x="9" y="9" width="12" height="12" rx="2" />
            <path d="M5 15V5a2 2 0 0 1 2-2h10" />
        </Svg>
    ),
    Sun: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
        </Svg>
    ),
    Moon: (p: IconProps) => (
        <Svg {...p}>
            <path d="M21 13A9 9 0 1 1 11 3a7 7 0 0 0 10 10z" />
        </Svg>
    ),
    Logout: (p: IconProps) => (
        <Svg {...p}>
            <path d="M10 17l5-5-5-5M15 12H3M21 4v16" />
        </Svg>
    ),
    Shield: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 2l8 3v7c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V5z" />
        </Svg>
    ),
    Key: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="8" cy="15" r="4" />
            <path d="M10.8 12.2L20 3M15 8l3 3M17 6l3 3" />
        </Svg>
    ),
    Lock: (p: IconProps) => (
        <Svg {...p}>
            <rect x="4" y="11" width="16" height="10" rx="2" />
            <path d="M8 11V7a4 4 0 0 1 8 0v4" />
        </Svg>
    ),
    Activity: (p: IconProps) => (
        <Svg {...p}>
            <path d="M3 12h4l3-8 4 16 3-8h4" />
        </Svg>
    ),
    Info: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="12" cy="12" r="9" />
            <path d="M12 11v5M12 8h.01" />
        </Svg>
    ),
    Alert: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 3l10 18H2zM12 10v4M12 17h.01" />
        </Svg>
    ),
    Sparkles: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8zM19 17l.8 2.2L22 20l-2.2.8L19 23l-.8-2.2L16 20l2.2-.8zM5 2l.6 1.6L7 4l-1.4.4L5 6l-.6-1.6L3 4l1.4-.4z" />
        </Svg>
    ),
    Table: (p: IconProps) => (
        <Svg {...p}>
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <path d="M3 10h18M9 10v10M15 10v10" />
        </Svg>
    ),
    Link: (p: IconProps) => (
        <Svg {...p}>
            <path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" />
        </Svg>
    ),
    Eye: (p: IconProps) => (
        <Svg {...p}>
            <path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z" />
            <circle cx="12" cy="12" r="3" />
        </Svg>
    ),
    EyeOff: (p: IconProps) => (
        <Svg {...p}>
            <path d="M3 3l18 18M10.6 10.6a3 3 0 0 0 4.2 4.2M9.9 5.2A10.4 10.4 0 0 1 12 5c6 0 10 7 10 7a17 17 0 0 1-3.2 3.9M6.6 6.6A16.6 16.6 0 0 0 2 12s4 7 10 7a9.7 9.7 0 0 0 4.4-1" />
        </Svg>
    ),
    Code: (p: IconProps) => (
        <Svg {...p}>
            <path d="M8 8l-4 4 4 4M16 8l4 4-4 4M14 4l-4 16" />
        </Svg>
    ),
    Clock: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v5l3 2" />
        </Svg>
    ),
    Zap: (p: IconProps) => (
        <Svg {...p}>
            <path d="M13 2L4 14h7l-1 8 9-12h-7z" />
        </Svg>
    ),
    ExternalLink: (p: IconProps) => (
        <Svg {...p}>
            <path d="M14 4h6v6M20 4l-9 9M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" />
        </Svg>
    ),
    Menu: (p: IconProps) => (
        <Svg {...p}>
            <path d="M4 7h16M4 12h16M4 17h16" />
        </Svg>
    ),
    Target: (p: IconProps) => (
        <Svg {...p}>
            <circle cx="12" cy="12" r="9" />
            <circle cx="12" cy="12" r="5" />
            <circle cx="12" cy="12" r="1" />
        </Svg>
    ),
    Upload: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 16V4M6 10l6-6 6 6M4 20h16" />
        </Svg>
    ),
    Layers: (p: IconProps) => (
        <Svg {...p}>
            <path d="M12 3l9 5-9 5-9-5zM3 13l9 5 9-5M3 17l9 5 9-5" />
        </Svg>
    ),
};

export type IconName = keyof typeof Icons;
