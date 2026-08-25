-- R5: grant Books access to every user who holds the legacy ComicSiteAccess key.
-- ComicSiteAccess (its value is the standalone site's URL) is LEFT IN PLACE: the NavBar's external
-- "Comics" link keeps working until R8 replaces it with the in-site section, which also deletes the key.
-- Run END-TO-END through SqlConnection (deploy-db-ops): count, insert, re-count, in one script.

SELECT COUNT(*) AS ComicSiteAccessRows FROM UserSettings WHERE SettingKey = 'ComicSiteAccess';
SELECT COUNT(*) AS BooksAccessRowsBefore FROM UserSettings WHERE SettingKey = 'BooksAccess';

INSERT INTO UserSettings (UserID, SettingKey, SettingValue)
SELECT c.UserID, 'BooksAccess', 'true'
FROM UserSettings c
WHERE c.SettingKey = 'ComicSiteAccess'
  AND NOT EXISTS (SELECT 1 FROM UserSettings b WHERE b.UserID = c.UserID AND b.SettingKey = 'BooksAccess');

SELECT COUNT(*) AS BooksAccessRowsAfter FROM UserSettings WHERE SettingKey = 'BooksAccess';
