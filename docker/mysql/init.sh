#!/bin/sh
# First start of the MySQL container only (docker-entrypoint-initdb.d): the same database and
# least-privilege accounts as db/setup-local.sql, but reachable from the API container ('%').
# Passwords come from the environment (.env); use letters, digits, '-' and '_' only.
set -eu

mysql --user=root --password="$MYSQL_ROOT_PASSWORD" <<SQL
CREATE DATABASE IF NOT EXISTS corefoundry CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER IF NOT EXISTS 'cf_meta'@'%' IDENTIFIED BY '${CF_META_PASSWORD}';
GRANT ALL PRIVILEGES ON corefoundry.* TO 'cf_meta'@'%';

-- cf_engine: only the per-project databases cf_p_* ('_' escaped: it is a wildcard in grants).
CREATE USER IF NOT EXISTS 'cf_engine'@'%' IDENTIFIED BY '${CF_ENGINE_PASSWORD}';
GRANT CREATE, ALTER, DROP, INDEX, REFERENCES, SELECT, INSERT, UPDATE, DELETE ON \`cf\_p\_%\`.* TO 'cf_engine'@'%';

FLUSH PRIVILEGES;
SQL
