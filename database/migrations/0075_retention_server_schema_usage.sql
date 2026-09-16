-- Retention functions already grant EXECUTE to the server role. Name resolution
-- also requires schema USAGE; table access and mutation authorization stay closed.
SET LOCAL ROLE sqlobserver_migrator;
GRANT USAGE ON SCHEMA system TO sqlobserver_server;
