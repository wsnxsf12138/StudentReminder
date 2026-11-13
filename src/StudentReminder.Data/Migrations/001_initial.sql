CREATE TABLE sources (
    id TEXT PRIMARY KEY, type TEXT NOT NULL, account_alias TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN (0,1)),
    health TEXT NOT NULL DEFAULT 'disabled', last_ok_at TEXT,
    UNIQUE(type, account_alias)
);
CREATE TABLE conversations (
    id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id),
    external_id TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
    selected INTEGER NOT NULL DEFAULT 0 CHECK(selected IN (0,1)), last_seen_at TEXT,
    UNIQUE(source_id, external_id)
);
CREATE TABLE messages (
    id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id),
    conversation_id TEXT NOT NULL REFERENCES conversations(id), external_id TEXT,
    dedup_key TEXT NOT NULL UNIQUE, sent_at TEXT NOT NULL, received_at TEXT NOT NULL,
    text TEXT NOT NULL, content_hash TEXT NOT NULL, payload_json TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('active','recalled','conflict'))
);
CREATE UNIQUE INDEX ix_message_external ON messages(source_id, conversation_id, external_id)
    WHERE external_id IS NOT NULL;
CREATE INDEX ix_message_time ON messages(sent_at DESC, id);
CREATE TABLE message_conflicts (
    message_id TEXT NOT NULL REFERENCES messages(id), payload_hash TEXT NOT NULL,
    payload_json TEXT NOT NULL, detected_at TEXT NOT NULL,
    PRIMARY KEY(message_id, payload_hash)
);
CREATE TABLE sync_cursors (
    source_id TEXT NOT NULL REFERENCES sources(id),
    conversation_id TEXT NOT NULL REFERENCES conversations(id),
    cursor_json TEXT NOT NULL, updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id, conversation_id)
);
CREATE TABLE event_candidates (
    id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, confidence TEXT NOT NULL,
    review_status TEXT NOT NULL DEFAULT 'pending', created_at TEXT NOT NULL
);
CREATE TABLE events (
    id TEXT PRIMARY KEY, title TEXT NOT NULL, starts_at TEXT, deadline_at TEXT,
    status TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL
);
CREATE TABLE event_evidence (
    event_id TEXT NOT NULL REFERENCES events(id), message_id TEXT NOT NULL REFERENCES messages(id),
    relation TEXT NOT NULL, PRIMARY KEY(event_id, message_id)
);
CREATE TABLE reminders (
    id TEXT PRIMARY KEY, event_id TEXT NOT NULL REFERENCES events(id),
    reminder_rule TEXT NOT NULL, fire_at TEXT NOT NULL, status TEXT NOT NULL, last_error TEXT,
    UNIQUE(event_id, reminder_rule, fire_at)
);
CREATE TABLE digests (
    id TEXT PRIMARY KEY, digest_date TEXT NOT NULL UNIQUE, content_json TEXT NOT NULL, generated_at TEXT NOT NULL
);
CREATE TABLE app_settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL);
