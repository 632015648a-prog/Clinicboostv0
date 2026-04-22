@echo off
setlocal EnableDelayedExpansion
rem ClinicBoost: Supabase (Docker) + API + Web — Windows (sin bash/WSL)
rem Uso (desde la raiz del repo o doble clic):
rem   scripts\dev-play.cmd
rem   scripts\dev-play.cmd --db-reset
rem   scripts\dev-play.cmd --no-supabase

cd /d "%~dp0.."
set "ROOT=%CD%"

set "DB_RESET="
set "NO_SB="
:parse
if "%~1"=="" goto parse_done
if /i "%~1"=="--db-reset" set "DB_RESET=1" & shift & goto parse
if /i "%~1"=="--no-supabase" set "NO_SB=1" & shift & goto parse
echo Argumento desconocido: %1
exit /b 1
:parse_done

if defined NO_SB goto skip_supabase
docker info >nul 2>&1
if errorlevel 1 (
  echo Docker no responde. Arranca Docker Desktop y reintenta.
  exit /b 1
)
echo.
echo  Supabase (docker)...
where supabase >nul 2>&1
if not errorlevel 1 (
  supabase start
  if errorlevel 1 exit /b 1
  if defined DB_RESET (
    echo Base de datos: reset (migraciones + seed^)...
    supabase db reset
  ) else (
    echo Base de datos: push migraciones...
    supabase db push
  )
) else (
  where npx >nul 2>&1
  if errorlevel 1 (
    echo No se encontro supabase ni npx. Instala: npm i -g supabase
    exit /b 1
  )
  call npx --yes supabase start
  if errorlevel 1 exit /b 1
  if defined DB_RESET (
    echo Base de datos: reset (migraciones + seed^)...
    call npx --yes supabase db reset
  ) else (
    echo Base de datos: push migraciones...
    call npx --yes supabase db push
  )
)
:skip_supabase

if not exist "apps\web\.env.local" (
  if exist "apps\web\.env.local.example" (
    echo Creando apps\web\.env.local ...
    copy /Y "apps\web\.env.local.example" "apps\web\.env.local" >nul
  ) else (
    echo Falta apps\web\.env.local
    exit /b 1
  )
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo No se encontro dotnet. Instala .NET SDK 10: https://dot.net
  exit /b 1
)
where node >nul 2>&1
if errorlevel 1 (
  echo No se encontro node. Instala Node.js 20+
  exit /b 1
)
where npm >nul 2>&1
if errorlevel 1 (
  echo No se encontro npm.
  exit /b 1
)

echo.
echo  Abriendo API y Web en ventanas separadas (cierralas para detenerlas^).
start "ClinicBoost API" /D "%ROOT%\apps\api" cmd /k dotnet run --project src/ClinicBoost.Api
start "ClinicBoost Web" /D "%ROOT%\apps\web" cmd /k npm run dev

echo.
echo   App:    http://localhost:5173
echo   API:    http://localhost:5011/scalar
echo   Studio: http://127.0.0.1:54323
echo.
endlocal
exit /b 0
