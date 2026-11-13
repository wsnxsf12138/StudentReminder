CREATE TABLE source_configs_v3 (
    source_id TEXT PRIMARY KEY REFERENCES sources(id),
    adapter TEXT NOT NULL CHECK(adapter IN ('weflow','wememory','qqflow','weflow-cli')),
    port INTEGER NOT NULL CHECK(port BETWEEN 1024 AND 65535),
    poll_seconds INTEGER NOT NULL CHECK(poll_seconds BETWEEN 60 AND 300),
    lookback_days INTEGER NOT NULL CHECK(lookback_days BETWEEN 1 AND 30),
    use_push INTEGER NOT NULL DEFAULT 0 CHECK(use_push IN (0,1)),
    revision INTEGER NOT NULL DEFAULT 1,
    provider_account TEXT
);
INSERT INTO source_configs_v3(source_id,adapter,port,poll_seconds,lookback_days,use_push,revision)
SELECT source_id,adapter,port,poll_seconds,lookback_days,use_push,revision FROM source_configs;
DROP TABLE source_configs;
ALTER TABLE source_configs_v3 RENAME TO source_configs;
