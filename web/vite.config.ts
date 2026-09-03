import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

function rejectStoreTransportImports() {
    return {
        name: 'reject-store-transport-imports',
        transform(code: string, id: string) {
            if (id.includes('/src/stores/') && /from\s+['"][^'"]*\/api\//.test(code)) {
                throw new Error('Store modules cannot import transport modules.');
            }
        },
    };
}

export default defineConfig({
    plugins: [rejectStoreTransportImports(), react(), tailwindcss()],
    // macOS AirPlay Receiver squats on port 5000 and answers 403, so the API defaults to 5080.
    server: { proxy: { '/api': process.env.DATAPITCHER_API_URL ?? 'http://localhost:5080' } },
    test: {
        environment: 'happy-dom',
        setupFiles: ['./src/test/setup.ts'],
        include: ['src/**/*.test.{ts,tsx}'],
        exclude: ['e2e/**'],
        coverage: {
            provider: 'v8',
            include: ['src/**/*.{ts,tsx}'],
            exclude: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'src/api/generated/**'],
            thresholds: { statements: 100, branches: 100, functions: 100, lines: 100 },
        },
    },
});
