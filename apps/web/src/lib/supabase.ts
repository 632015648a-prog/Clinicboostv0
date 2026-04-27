import { createClient } from '@supabase/supabase-js'

const supabaseUrl  = import.meta.env.VITE_SUPABASE_URL  as string
const supabaseAnon = import.meta.env.VITE_SUPABASE_ANON_KEY as string

if (!supabaseUrl || !supabaseAnon) {
  throw new Error(
    'Faltan variables de entorno VITE_SUPABASE_URL o VITE_SUPABASE_ANON_KEY. ' +
    'Crea un archivo .env.local con esos valores.'
  )
}

/**
 * Cliente Supabase compartido.
 * Sesión en memoria de la pestaña; api.ts adjunta el access_token a cada llamada a la API .NET.
 */
export const supabase = createClient(supabaseUrl, supabaseAnon, {
  auth: {
    persistSession: false,
    autoRefreshToken: false,
    detectSessionInUrl: false,
  },
})
