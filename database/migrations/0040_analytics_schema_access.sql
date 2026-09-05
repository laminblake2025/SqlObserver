-- Runtime roles already have narrowly scoped EXECUTE grants on analytics
-- functions. Schema USAGE makes those grants reachable without granting
-- direct table access or the ability to create objects.
GRANT USAGE ON SCHEMA analytics TO sqlobserver_collector, sqlobserver_server;
