-- sqlobserver:nontransactional-index=security.ix_protected_payload_cleanup
CREATE INDEX CONCURRENTLY ix_protected_payload_cleanup
    ON security.protected_diagnostic_payload (payload_kind, created_at, payload_id);
