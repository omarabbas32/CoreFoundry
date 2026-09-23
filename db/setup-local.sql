-- CoreFoundry: one-time setup for a local MySQL server (8.4+).
--
-- Creates the metadata database and the two least-privilege accounts:
--   cf_meta   -> everything on `corefoundry`, nothing else
--   cf_engine -> DDL + DML on `cf_p_%` project databases only, no access to `corefoundry`
--
-- Run as root (or another admin), passing the passwords as session variables so
-- they never live in this file:
--
--   mysql -u root -p -e "SET @meta_pwd='...'; SET @engine_pwd='...'; SOURCE db/setup-local.sql;"
--
-- Safe to run again: existing accounts get their password reset and grants re-applied.

CREATE DATABASE IF NOT EXISTS corefoundry
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

SET @sql = CONCAT('CREATE USER IF NOT EXISTS ''cf_meta''@''localhost'' IDENTIFIED BY ', QUOTE(@meta_pwd));
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = CONCAT('ALTER USER ''cf_meta''@''localhost'' IDENTIFIED BY ', QUOTE(@meta_pwd));
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = CONCAT('CREATE USER IF NOT EXISTS ''cf_engine''@''localhost'' IDENTIFIED BY ', QUOTE(@engine_pwd));
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = CONCAT('ALTER USER ''cf_engine''@''localhost'' IDENTIFIED BY ', QUOTE(@engine_pwd));
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = NULL, @meta_pwd = NULL, @engine_pwd = NULL;

GRANT ALL PRIVILEGES ON corefoundry.* TO 'cf_meta'@'localhost';

-- '_' is a wildcard in grant patterns, so it is escaped: cf\_p\_%
GRANT CREATE, ALTER, DROP, INDEX, SELECT, INSERT, UPDATE, DELETE
  ON `cf\_p\_%`.* TO 'cf_engine'@'localhost';

FLUSH PRIVILEGES;

SELECT user, host FROM mysql.user WHERE user IN ('cf_meta', 'cf_engine');
