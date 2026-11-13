CREATE TABLE moect_analysis (
    message_id TEXT PRIMARY KEY REFERENCES messages(id),
    result_json TEXT,
    analyzed_at TEXT,
    last_attempt_at TEXT
);

CREATE INDEX ix_moect_analysis_pending ON moect_analysis(message_id) WHERE result_json IS NULL;

CREATE TABLE moect_imported_exports (
    target_key TEXT NOT NULL,
    digest TEXT NOT NULL,
    PRIMARY KEY(target_key, digest)
);
