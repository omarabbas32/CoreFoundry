-- CoreFoundry: generates src/CoreFoundry.Domain/Schema/Resources/mysql-reserved-words.txt,
-- the reserved words a table or column name may not use (IdentifierRules).
--
-- Run against a MySQL 8.4+ server (any account can read INFORMATION_SCHEMA.KEYWORDS):
--
--   mysql -N -B -u cf_meta -p < db/reserved-words.sql > src/CoreFoundry.Domain/Schema/Resources/mysql-reserved-words.txt
--
-- A newer server only adds words, so regenerating on a newer version is always safe.

SELECT LOWER(WORD) FROM INFORMATION_SCHEMA.KEYWORDS WHERE RESERVED = 1 ORDER BY LOWER(WORD);
