import { defineConfig } from 'orval';

export default defineConfig({
  client: {
    input: { target: './openapi/datapitcher.openapi.json' },
    output: { target: './src/api/generated/client.ts', client: 'fetch', mode: 'single' },
  },
  validation: {
    input: { target: './openapi/datapitcher.openapi.json' },
    output: { target: './src/api/generated/permissions.zod.ts', client: 'zod', mode: 'single' },
  },
});
