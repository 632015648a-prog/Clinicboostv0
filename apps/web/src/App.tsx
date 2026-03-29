import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'

// Pages (se añadirán conforme avance el proyecto)
import DashboardPage from './pages/DashboardPage'
import LoginPage     from './pages/LoginPage'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime:   1000 * 60 * 5,  // 5 min
      retry:       1,
      refetchOnWindowFocus: false,
    },
  },
})

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route path="/login"     element={<LoginPage />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="*"          element={<Navigate to="/dashboard" replace />} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
