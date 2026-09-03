# DataPitcher web client

A React 19 + Vite + Tailwind 4 client for the DataPitcher API. It covers the whole operator workflow: connections and
schema scans, the schema explorer with a dependency graph, the raw-SQL selection workbench, transfer-plan creation,
sealing and review, and a live transfer monitor fed by the job event stream.

## Run it locally

From the repository root:

```sh
./scripts/dev.sh
```

It builds and starts the API on port 5080 with the default local signing key, waits for it to become healthy, then
starts the Vite dev server on port 5173 and opens the browser. The sign-in page is prefilled with that key: pick roles
and click Sign in. Ctrl+C stops both processes. Override `API_PORT`, `WEB_PORT`, or
`Authentication__Development__SigningKey` in the environment if needed.

Manual alternative: start the API with `Authentication__Development__SigningKey`, `ASPNETCORE_ENVIRONMENT=Development`
and `ASPNETCORE_URLS=http://localhost:5080`, then `npm --prefix web run dev` (the proxy target is `DATAPITCHER_API_URL`,
default `http://localhost:5080`; port 5000 is avoided because macOS AirPlay Receiver answers 403 there).

Connection strings are never sent to the API. When you add a connection, the UI shows the exact
`DATAPITCHER_CREDENTIAL_<id>` environment variable to export before restarting the API.

## What the client remembers locally

The API has no "list plans" endpoint and saved selections carry no display name, so the client keeps a small registry
of plan and selection names in `localStorage` (`datapitcher.registry`). Plans created elsewhere can be opened by ID from
the Plans page. Theme and sidebar state live in `datapitcher.preferences`.

## Scripts

| Command | Purpose |
| --- | --- |
| `npm run dev` | Vite dev server with API proxy |
| `npm run build` | Type-check and production build |
| `npm run lint` | ESLint (a11y, hooks, store boundaries) |
| `npm test` | Vitest |
| `npm run generate:api` | Regenerate the OpenAPI client and Zod schemas |
