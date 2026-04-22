import axios from 'axios'

/**
 * Cliente HTTP para la API .NET (ClinicBoost.Api).
 * - withCredentials = true para enviar la cookie httpOnly en cada request.
 * - baseURL se configura vía variable de entorno.
 */
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? 'http://localhost:5011',
  withCredentials: true,       // ← cookies httpOnly
  headers: {
    'Content-Type': 'application/json',
  },
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
