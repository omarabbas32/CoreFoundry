-- CoreFoundry: one-time setup for the integration-test database on a local MySQL server.
--
-- Integration tests drop and re-create `corefoundry_test` on every run, so cf_meta needs
-- full rights on it. Run as root after db/setup-local.sql:
--
--   mysql -u root -p < db/setup-test.sql
--
-- Your development database `corefoundry` is never touched by tests.

CREATE DATABASE IF NOT EXISTS corefoundry_test
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

GRANT ALL PRIVILEGES ON corefoundry_test.* TO 'cf_meta'@'localhost';

FLUSH PRIVILEGES;
