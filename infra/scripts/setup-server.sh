#!/usr/bin/env bash
# ============================================================
# infra/scripts/setup-server.sh
#
# Prepara un servidor Ubuntu 22.04/24.04 limpio para ClinicBoost staging.
# Instala: Docker, Docker Compose v2, git, y crea el usuario de despliegue.
#
# USO (como root en el servidor):
#   curl -fsSL https://raw.githubusercontent.com/.../setup-server.sh | bash
#   # o
#   bash infra/scripts/setup-server.sh
#
# REQUISITOS:
#   · Ubuntu 22.04 LTS o 24.04 LTS
#   · Acceso root o sudo sin contraseña
#   · Dominio staging.clinicboost.es apuntando a la IP del servidor
# ============================================================

set -euo pipefail

DEPLOY_USER="deploy"
APP_DIR="/opt/clinicboost"
REPO_URL="${REPO_URL:-https://github.com/632015648a-prog/Clinicboostv0.git}"

log()  { echo -e "\033[0;32m[SETUP]\033[0m $*"; }
warn() { echo -e "\033[1;33m[WARN]\033[0m $*"; }

# ── 1. Actualizar sistema ─────────────────────────────────────────────────────
log "Actualizando sistema..."
apt-get update -q
apt-get upgrade -y -q
apt-get install -y -q curl git ufw fail2ban

# ── 2. Instalar Docker ────────────────────────────────────────────────────────
log "Instalando Docker CE..."
if ! command -v docker &> /dev/null; then
  curl -fsSL https://get.docker.com | sh
  systemctl enable docker
  systemctl start docker
else
  log "Docker ya instalado: $(docker --version)"
fi

# ── 3. Crear usuario de despliegue ────────────────────────────────────────────
log "Creando usuario ${DEPLOY_USER}..."
if ! id -u "${DEPLOY_USER}" &> /dev/null; then
  useradd -m -s /bin/bash "${DEPLOY_USER}"
  usermod -aG docker "${DEPLOY_USER}"
  log "Usuario ${DEPLOY_USER} creado y añadido al grupo docker."
else
  log "Usuario ${DEPLOY_USER} ya existe."
fi

# Crear directorio .ssh para la clave pública del CI
mkdir -p "/home/${DEPLOY_USER}/.ssh"
chmod 700 "/home/${DEPLOY_USER}/.ssh"
chown "${DEPLOY_USER}:${DEPLOY_USER}" "/home/${DEPLOY_USER}/.ssh"

log "ACCIÓN MANUAL: añadir la clave pública SSH del CI a:"
log "  /home/${DEPLOY_USER}/.ssh/authorized_keys"

# ── 4. Clonar repositorio ─────────────────────────────────────────────────────
log "Clonando repositorio en ${APP_DIR}..."
if [ ! -d "${APP_DIR}/.git" ]; then
  git clone "${REPO_URL}" "${APP_DIR}"
  chown -R "${DEPLOY_USER}:${DEPLOY_USER}" "${APP_DIR}"
else
  log "Repositorio ya existe en ${APP_DIR}."
fi

# ── 5. Crear directorios de logs ──────────────────────────────────────────────
mkdir -p "${APP_DIR}/logs/nginx" "${APP_DIR}/logs/api"
chown -R "${DEPLOY_USER}:${DEPLOY_USER}" "${APP_DIR}/logs"

# ── 6. Configurar firewall (UFW) ──────────────────────────────────────────────
log "Configurando firewall UFW..."
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp    comment "SSH"
ufw allow 80/tcp    comment "HTTP"
ufw allow 443/tcp   comment "HTTPS"
ufw --force enable
log "UFW configurado (22, 80, 443 abiertos)."

# ── 7. Configurar fail2ban ────────────────────────────────────────────────────
log "Configurando fail2ban..."
systemctl enable fail2ban
systemctl start fail2ban

# ── 8. Verificación final ─────────────────────────────────────────────────────
log "=== Verificación final ==="
log "Docker:         $(docker --version)"
log "Docker Compose: $(docker compose version)"
log "Git:            $(git --version)"
log ""
log "=== SIGUIENTES PASOS MANUALES ==="
log "1. Añadir clave SSH del CI: /home/${DEPLOY_USER}/.ssh/authorized_keys"
log "2. Copiar .env.staging al servidor: scp .env.staging ${DEPLOY_USER}@<IP>:${APP_DIR}/"
log "3. Primer deploy manual:"
log "   cd ${APP_DIR}"
log "   docker compose -f docker-compose.staging.yml --env-file .env.staging up -d"
log ""
warn "Dominio configurado: asegúrate de que staging.clinicboost.es → $(curl -4 -s ifconfig.me)"
