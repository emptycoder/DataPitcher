import { useState, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HttpError } from '../api/http';
import { ToastProvider } from '../ui/toast';

export function createAppQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 10_000,
        refetchOnWindowFocus: true,
        retry: (failureCount, error) => {
          if (error instanceof HttpError && error.status < 500) return false;
          return failureCount < 2;
        },
      },
    },
  });
}

export function AppProviders({ children, client }: Readonly<{ children: ReactNode; client?: QueryClient }>) {
  const [queryClient] = useState(() => client ?? createAppQueryClient());
  return (
    <QueryClientProvider client={queryClient}>
      <ToastProvider>{children}</ToastProvider>
    </QueryClientProvider>
  );
}
