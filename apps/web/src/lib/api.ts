import axios from 'axios'
import { supabase } from './supabase'

/**
 * Cliente HTTP para la API .NET (ClinicBoost.Api).
 * - withCredentials = true (cookies si el backend las define).
 * - Authorization: Bearer <access_token> desde la sesión de Supabase (la API lee JWT
 *   desde cookie sb-access-token O desde este header — ver ServiceCollectionExtensions).
 */
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? 'http://localhost:5011',
  withCredentials: true,
  headers: {
    'Content-Type': 'application/json',
  },
})

api.interceptors.request.use(async (config) => {
  const { data } = await supabase.auth.getSession()
  const token = data.session?.access_token
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// Interceptor: si el backend devuelve 401, redirigir al login
api.interceptors.response.use(
  (res) => res,
  (err) => {
    if (err.response?.status === 401) {
      window.location.href = '/login'
    }
    return Promise.reject(err)
  }
)
