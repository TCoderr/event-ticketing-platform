#!/bin/sh
# Yalnız veritabanı ilk kez oluşturulurken çalışır (resmi postgres imajı davranışı).
# Şema sahibi (migration) ve çalışma zamanı rollerini ayrı parolalarla oluşturur.
set -eu

owner_password="$(cat "$LOCA_OWNER_PASSWORD_FILE")"
app_password="$(cat "$LOCA_APP_PASSWORD_FILE")"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v owner_password="$owner_password" -v app_password="$app_password" <<'SQL'
CREATE ROLE loca_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE loca_app   LOGIN PASSWORD :'app_password';
ALTER DATABASE :"DBNAME" OWNER TO loca_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
