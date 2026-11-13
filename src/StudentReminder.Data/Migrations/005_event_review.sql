CREATE TABLE source_configs_v5 (
    source_id TEXT PRIMARY KEY REFERENCES sources(id),
    adapter TEXT NOT NULL CHECK(adapter IN ('weflow','wememory','qqflow','weflow-cli','wxlocal')),
    port INTEGER NOT NULL CHECK((adapter='wxlocal' AND port=0) OR (adapter<>'wxlocal' AND port BETWEEN 1024 AND 65535)),
    poll_seconds INTEGER NOT NULL CHECK(poll_seconds BETWEEN 60 AND 300),
    lookback_days INTEGER NOT NULL CHECK(lookback_days BETWEEN 1 AND 30),
    use_push INTEGER NOT NULL DEFAULT 0 CHECK(use_push IN (0,1)),
    revision INTEGER NOT NULL DEFAULT 1,
    provider_account TEXT
);
INSERT INTO source_configs_v5 SELECT source_id,adapter,port,poll_seconds,lookback_days,use_push,revision,provider_account
FROM source_configs;
DROP TABLE source_configs;
ALTER TABLE source_configs_v5 RENAME TO source_configs;

ALTER TABLE messages ADD COLUMN wxlocal_ingested_at TEXT;
CREATE INDEX ix_message_analysis_scope ON messages(conversation_id,wxlocal_ingested_at,sent_at)
    WHERE status='active' AND wxlocal_ingested_at IS NOT NULL;
CREATE INDEX ix_message_history_scope ON messages(conversation_id,sent_at,id);

CREATE TABLE ai_analysis (
    message_id TEXT PRIMARY KEY REFERENCES messages(id),
    status TEXT NOT NULL CHECK(status IN ('filtered','retry','failed','empty','extracted')),
    attempts INTEGER NOT NULL DEFAULT 0,
    retry_at TEXT,
    error_code TEXT,
    last_request_id INTEGER REFERENCES ai_requests(id)
);
CREATE TABLE ai_requests (
    id INTEGER PRIMARY KEY,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    sent_at TEXT NOT NULL,
    local_day TEXT NOT NULL,
    prompt_tokens INTEGER,
    completion_tokens INTEGER
);
CREATE INDEX ix_ai_requests_day ON ai_requests(local_day);
CREATE TABLE ai_provider_cooldowns (
    provider TEXT PRIMARY KEY,
    available_at TEXT NOT NULL
);

ALTER TABLE event_candidates ADD COLUMN event_id TEXT REFERENCES events(id);
ALTER TABLE event_candidates ADD COLUMN fingerprint TEXT;
ALTER TABLE event_candidates ADD COLUMN change_kind TEXT NOT NULL DEFAULT 'new';
CREATE INDEX ix_candidate_review ON event_candidates(review_status,created_at);
CREATE INDEX ix_candidate_fingerprint ON event_candidates(fingerprint);
CREATE TABLE candidate_evidence (
    candidate_id TEXT NOT NULL REFERENCES event_candidates(id),
    message_id TEXT NOT NULL REFERENCES messages(id),
    PRIMARY KEY(candidate_id,message_id)
);
CREATE INDEX ix_candidate_evidence_message ON candidate_evidence(message_id);
CREATE TABLE event_versions (
    event_id TEXT NOT NULL REFERENCES events(id),
    version INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    status TEXT NOT NULL,
    candidate_id TEXT REFERENCES event_candidates(id),
    changed_at TEXT NOT NULL,
    PRIMARY KEY(event_id,version)
);
