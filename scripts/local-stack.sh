#!/usr/bin/env bash
# Loca yerel stack'i (PostgreSQL + API/SPA, HTTPS) — Linux / macOS / WSL / Git Bash.
# scripts/local-stack.ps1 ile aynı komutlar ve aynı durum dizini düzeni.
#
#   init    Durum dizinini ve secret dosyalarını (yoksa) üretir, imajı derler, stack'i başlatır,
#           migration'ı uygular, demo verisini ekler. Tekrar çalıştırmak güvenlidir.
#   up      Var olan stack'i başlatır (veri korunur).
#   down    Stack'i durdurur. Volume ve secret dosyalarını SİLMEZ.
#   status  Servis durumu ve hazır olma sonucu.
#   logs    Servis logları (isteğe bağlı servis adı).
#   seed    Demo verisini yeniden uygular (idempotent).
#   reset   YIKICI: bu stack'in volume'larını ve durum dizinini siler; --confirm-project <ad> ister.
#
# Kullanım: scripts/local-stack.sh <komut> [--project loca] [--https-port 8643] [--no-build]
#           scripts/local-stack.sh reset --confirm-project loca
set -euo pipefail

command="${1:-}"; shift || true
project="loca"; https_port="8643"; confirm_project=""; no_build="false"; service=""
while [ $# -gt 0 ]; do
  case "$1" in
    --project) project="$2"; shift 2 ;;
    --https-port) https_port="$2"; shift 2 ;;
    --confirm-project) confirm_project="$2"; shift 2 ;;
    --no-build) no_build="true"; shift ;;
    --service) service="$2"; shift 2 ;;
    *) echo "Bilinmeyen seçenek: $1" >&2; exit 2 ;;
  esac
done

case "$command" in init|up|down|status|logs|seed|reset) ;; *)
  echo "Kullanım: $0 {init|up|down|status|logs|seed|reset} [--project ad] [--https-port port]" >&2; exit 2 ;;
esac
if ! printf '%s' "$project" | grep -Eq '^[a-z][a-z0-9-]{1,40}$'; then
  echo "Geçersiz proje adı: $project" >&2; exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
state_dir="$repo_root/.local/$project"
manifest="$state_dir/stack.manifest.json"
secrets_dir="$state_dir/secrets"; users_dir="$state_dir/users"; certs_dir="$state_dir/certs"

demo_users="platform-admin@loca.demo organizer-kumru@loca.demo organizer-marti@loca.demo gate-kumru@loca.demo customer-ayse@loca.demo customer-mehmet@loca.demo"
secret_files="db-superuser.password.txt db-owner.password.txt db-app.password.txt psp-api-key.secret.txt psp-webhook.secret.txt"

step() { printf '==> %s\n' "$*"; }
random_hex() { head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; }
manifest_value() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p" "$manifest" | head -n 1; }

docker info --format '{{.ServerVersion}}' >/dev/null 2>&1 || { echo "Docker Engine çalışmıyor veya erişilemiyor." >&2; exit 1; }

write_secret() { # dosya varsa asla üzerine yazılmaz
  if [ -e "$1" ]; then return 1; fi
  printf '%s' "$2" > "$1"
  # Konteyner kullanıcıları (postgres: 999, api: 1654) okuyabilmeli; koruma üst dizinin 0700 olmasıyla sağlanır.
  chmod 0644 "$1" 2>/dev/null || true
  return 0
}

initialize_state() {
  if [ ! -f "$manifest" ]; then
    mkdir -p "$secrets_dir" "$users_dir" "$certs_dir"
    chmod 0700 "$state_dir" 2>/dev/null || true
    printf '{\n  "projectName": "%s",\n  "httpsPort": %s,\n  "stackId": "%s",\n  "createdAt": "%s"\n}\n' \
      "$project" "$https_port" "$(random_hex 16)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$manifest"
    step "Durum dizini oluşturuldu: $state_dir"
  else
    [ "$(manifest_value projectName)" = "$project" ] || { echo "Manifest başka bir projeye ait." >&2; exit 1; }
    mkdir -p "$secrets_dir" "$users_dir" "$certs_dir"
  fi
  created=0
  for file in $secret_files; do
    if write_secret "$secrets_dir/$file" "$(random_hex 24)"; then created=$((created + 1)); fi
  done
  for user in $demo_users; do
    if write_secret "$users_dir/$user.password.txt" "Demo-$(random_hex 9)-7a"; then created=$((created + 1)); fi
  done
  if [ "$created" -gt 0 ]; then step "$created yeni secret/parola dosyası üretildi (değerler ekrana yazılmaz)."; fi
}

compose() {
  [ -f "$manifest" ] || { echo "Bu proje için durum dizini yok ($state_dir). Önce 'init' çalıştırın." >&2; exit 1; }
  LOCA_STATE_DIR="$state_dir" LOCA_HTTPS_PORT="$(manifest_value httpsPort)" LOCA_STACK_ID="$(manifest_value stackId)" \
    docker compose --project-name "$project" --project-directory "$repo_root" --file "$repo_root/compose.yaml" "$@"
}

export_certificate() {
  if compose cp api:/var/lib/loca/keys/loca-local.crt "$certs_dir/loca-local.crt" >/dev/null 2>&1; then
    step "Sunucu sertifikası dışa aktarıldı: $certs_dir/loca-local.crt"
  fi
}

summary() {
  printf '\nLoca çalışıyor:  https://localhost:%s\n' "$(manifest_value httpsPort)"
  printf '  Sertifika self-signed'"'"'dır; tarayıcı ilk açılışta uyarı gösterir.\n'
  printf '  Demo kullanıcı parolaları: %s/<e-posta>.password.txt\n' "$users_dir"
  for user in $demo_users; do printf '    %s\n' "$user"; done
}

case "$command" in
  init)
    initialize_state
    if [ "$no_build" != "true" ]; then step "İmaj derleniyor"; compose build migrate; fi
    step "Stack başlatılıyor (migration dahil)"
    compose up --detach --wait --wait-timeout 180
    step "Demo verisi uygulanıyor (idempotent)"
    compose run --rm --no-deps api seed-demo
    export_certificate
    summary ;;
  up)
    compose up --detach --wait --wait-timeout 180
    export_certificate
    summary ;;
  down)
    compose down   # volume'lar ve secret dosyaları korunur
    step "Stack durduruldu. Veri korunuyor; yeniden açmak için: up" ;;
  status)
    compose ps --all
    if [ -f "$certs_dir/loca-local.crt" ]; then
      code="$(curl --silent --output /dev/null --write-out '%{http_code}' --cacert "$certs_dir/loca-local.crt" --max-time 5 "https://localhost:$(manifest_value httpsPort)/health/ready" || true)"
      echo "health/ready: HTTP $code"
    fi ;;
  logs)
    if [ -n "$service" ]; then compose logs --tail 200 "$service"; else compose logs --tail 200; fi ;;
  seed)
    compose run --rm --no-deps api seed-demo ;;
  reset)
    [ -f "$manifest" ] || { echo "Silinecek stack bulunamadı: $state_dir" >&2; exit 1; }
    if [ "$confirm_project" != "$project" ]; then
      echo "reset yıkıcıdır. Onaylamak için: --confirm-project $project" >&2; exit 1
    fi
    stack_id="$(manifest_value stackId)"
    for volume in "${project}_pgdata" "${project}_keys"; do
      if labels="$(docker volume inspect "$volume" --format '{{json .Labels}}' 2>/dev/null)"; then
        case "$labels" in *"\"loca.stack-id\":\"$stack_id\""*) ;; *)
          echo "Volume '$volume' bu stack'e ait görünmüyor (etiket uyuşmuyor). Hiçbir şey silinmedi." >&2; exit 1 ;;
        esac
      fi
    done
    step "Stack '$project' ve volume'ları siliniyor"
    compose down --volumes --remove-orphans
    case "$state_dir" in "$repo_root/.local/"?*) rm -rf -- "$state_dir" ;; *) echo "Durum dizini beklenen konumda değil; silinmedi." >&2; exit 1 ;; esac
    step "Silindi." ;;
esac
