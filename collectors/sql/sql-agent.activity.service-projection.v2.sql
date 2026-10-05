SELECT CONVERT(bit, CASE WHEN CHARINDEX(N'sqlagent.exe', LOWER(service.filename)) > 0 THEN 1 ELSE 0 END) AS candidate,
       CONVERT(bit, CASE WHEN token.delimited = 1
          AND CHARINDEX(N'"', token.path) = 0
          AND CHARINDEX(NCHAR(9), token.path) = 0 AND CHARINDEX(NCHAR(10), token.path) = 0
          AND CHARINDEX(NCHAR(11), token.path) = 0 AND CHARINDEX(NCHAR(12), token.path) = 0
          AND CHARINDEX(NCHAR(13), token.path) = 0
          AND LOWER(RIGHT(token.path, CHARINDEX(N'/', REVERSE(token.path) + N'/') - 1)) COLLATE Latin1_General_100_BIN2 = N'sqlagent.exe'
          AND (LEFT(normalized.path, 1) = N'"' OR CHARINDEX(N' ', token.path) = 0)
          THEN 1 ELSE 0 END) AS valid_path,
       service.status, service.process_id AS pid, service.last_startup_time AS startup
FROM sys.dm_server_services AS service
CROSS APPLY (SELECT LTRIM(RTRIM(REPLACE(service.filename, N'\', N'/'))) AS path) AS normalized
CROSS APPLY (SELECT CHARINDEX(N'"', normalized.path, 2) AS closing_quote) AS quoted
CROSS APPLY (SELECT
    CASE WHEN LEFT(normalized.path, 1) = N'"' THEN
        CASE WHEN quoted.closing_quote > 1 THEN SUBSTRING(normalized.path, 2, quoted.closing_quote - 2) ELSE N'' END
        ELSE normalized.path END AS path,
    CASE WHEN LEFT(normalized.path, 1) <> N'"' THEN 1
         WHEN quoted.closing_quote > 1 AND (quoted.closing_quote = LEN(normalized.path)
           OR UNICODE(SUBSTRING(normalized.path, quoted.closing_quote + 1, 1)) IN (9,32)) THEN 1
         ELSE 0 END AS delimited) AS token
