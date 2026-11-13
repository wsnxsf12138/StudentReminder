CREATE TABLE source_configs (
    source_id TEXT PRIMARY KEY REFERENCES sources(id),
    adapter TEXT NOT NULL CHECK(adapter IN ('weflow','wememory','qqflow')),
    port INTEGER NOT NULL CHECK(port BETWEEN 1024 AND 65535),
    poll_seconds INTEGER NOT NULL CHECK(poll_seconds BETWEEN 60 AND 300),
    lookback_days INTEGER NOT NULL CHECK(lookback_days BETWEEN 1 AND 30),
    use_push INTEGER NOT NULL DEFAULT 0 CHECK(use_push IN (0,1)),
    revision INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ix_one_wechat_collector ON sources(type) WHERE type='wechat' AND enabled=1;
CREATE TABLE recall_markers (
    source_id TEXT NOT NULL REFERENCES sources(id), conversation_external_id TEXT NOT NULL,
    message_external_id TEXT NOT NULL, recalled_at TEXT NOT NULL,
    PRIMARY KEY(source_id, conversation_external_id, message_external_id)
);
