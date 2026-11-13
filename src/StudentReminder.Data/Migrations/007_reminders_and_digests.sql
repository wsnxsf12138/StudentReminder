CREATE INDEX ix_reminders_status_fire ON reminders(status,fire_at,id);
ALTER TABLE reminders ADD COLUMN reported_at TEXT;
ALTER TABLE digests ADD COLUMN scheduled_at TEXT;

UPDATE sources SET enabled=0,health='disabled',last_ok_at=NULL
WHERE id IN (SELECT source_id FROM source_configs WHERE adapter NOT IN ('wxlocal','qqlocal'));
UPDATE conversations SET selected=0
WHERE source_id IN (SELECT source_id FROM source_configs WHERE adapter NOT IN ('wxlocal','qqlocal'));
DELETE FROM sync_cursors
WHERE source_id IN (SELECT source_id FROM source_configs WHERE adapter NOT IN ('wxlocal','qqlocal'));

CREATE TABLE source_configs_v7 (
    source_id TEXT PRIMARY KEY REFERENCES sources(id),
    adapter TEXT NOT NULL CHECK(adapter IN ('wxlocal','qqlocal')),
    port INTEGER NOT NULL CHECK(port=0),
    poll_seconds INTEGER NOT NULL CHECK(poll_seconds BETWEEN 60 AND 300),
    lookback_days INTEGER NOT NULL CHECK(lookback_days BETWEEN 1 AND 30),
    use_push INTEGER NOT NULL DEFAULT 0 CHECK(use_push IN (0,1)),
    revision INTEGER NOT NULL DEFAULT 1,
    provider_account TEXT
);
INSERT INTO source_configs_v7
SELECT source_id,adapter,port,poll_seconds,lookback_days,use_push,revision,provider_account
FROM source_configs WHERE adapter IN ('wxlocal','qqlocal');
DROP TABLE source_configs;
ALTER TABLE source_configs_v7 RENAME TO source_configs;
