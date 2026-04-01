import { useState, useEffect } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Routes, Route, Navigate, useNavigate } from 'react-router-dom'
import { supabase } from './lib/supabase'

import DashboardPage from './pages/DashboardPage'
import LoginPage     from './pages/LoginPage'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime:           1000 * 60 * 2,   // 2 min
      retry:               1,
      refetchOnWindowFocus: false,
    },
  },
})

/**
 * Guarda de autenticación.
 * Si no hay sesión activa de Supabase, redirige a /login.
 */
function AuthGuard({ children }: { children: React.ReactNode }) {
  const navigate  = useNavigate()
  const [checked, setChecked] = useState(false)

  useEffect(() => {
    // Comprobación inicial de sesión
    supabase.auth.getSession().then(({ data }) => {
      if (!data.session) navigate('/login', { replace: true })
      setChecked(true)
    })

    // Listener para cambios de sesión (logout desde otra pestaña, expiración, etc.)
    const { data: { subscription } } = supabase.auth.onAuthStateChange((_event, session) => {
      if (!session) navigate('/login', { replace: true })
    })

    return () => subscription.unsubscribe()
  }, [navigate])

  if (!checked) {
    // Pantalla de carga mínima mientras verificamos la sesión
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-500 border-t-transparent" />
      </div>
    )
  }

  return <>{children}</>
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginPage />} />

          {/* Rutas protegidas */}
          <Route
            path="/dashboard"
            element={
              <AuthGuard>
                <DashboardPage />
              </AuthGuard>
            }
          />

          {/* Fallback */}
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
