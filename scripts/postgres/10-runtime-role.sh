#!/bin/sh
set -eu

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${APP_DB_PASSWORD:?APP_DB_PASSWORD is required}"

if [ "$POSTGRES_USER" = "retailops_runtime" ]; then
    echo 'POSTGRES_USER must be the migration owner, not retailops_runtime.' >&2
    exit 1
fi

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=db_owner="$POSTGRES_USER" --set=db_name="$POSTGRES_DB" \
    --set=runtime_password="$APP_DB_PASSWORD" <<'SQL'
SELECT 'CREATE ROLE retailops_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'retailops_runtime')
\gexec
ALTER ROLE retailops_runtime WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'runtime_password';
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM retailops_runtime;
REVOKE TEMPORARY ON DATABASE :"db_name" FROM PUBLIC;
REVOKE CREATE, TEMPORARY ON DATABASE :"db_name" FROM retailops_runtime;
GRANT CONNECT ON DATABASE :"db_name" TO retailops_runtime;
GRANT USAGE ON SCHEMA public TO retailops_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO retailops_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO retailops_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE :"db_owner" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO retailops_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE :"db_owner" IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO retailops_runtime;
SQL
