import { useState, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

export function AppProviders({ children, client }: { children: ReactNode; client?: QueryClient }) {
  const [queryClient] = useState(() => client ?? new QueryClient());
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
