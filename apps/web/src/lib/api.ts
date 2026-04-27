import axios from 'axios'
import { supabase } from './supabase'

/**
 * Cliente HTTP para la API .NET (ClinicBoost.Api).
 * - withCredentials = true para enviar la cookie httpOnly en cada request.
 * - baseURL se configura vía variable de entorno.
 */
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? 'http://localhost:5011',
  withCredentials: true,
  headers: {
    'Content-Type': 'application/json',
  },
})

// Interceptor de request: adjunta el access token de Supabase como Bearer.
// El backend acepta JWT tanto por cookie (sb-access-token) como por header
// Authorization. En desarrollo local sin HTTPS las cookies Secure no se
// envían cross-port, así que usamos el header.
api.interceptors.request.use(async (config) => {
  const { data: { session } } = await supabase.auth.getSession()
  if (session?.access_token) {
    config.headers.Authorization = `Bearer ${session.access_token}`
  }
  return config
})

// Interceptor de response: si el backend devuelve 401, redirigir al login
api.interceptors.response.use(
  (res) => res,
  (err) => {
    if (err.response?.status === 401) {
      window.location.href = '/login'
    }
    return Promise.reject(err)
  }
)
