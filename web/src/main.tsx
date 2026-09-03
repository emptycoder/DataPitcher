import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { AppProviders } from './app/AppProviders';
import { injectStorage } from './stores/persistence';
import './styles.css';

// Composition root: the only place browser storage is handed to the stores.
injectStorage(globalThis.localStorage ?? null);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppProviders>
      <App />
    </AppProviders>
  </StrictMode>,
);
