CREATE INDEX ix_message_hybrid_context ON messages(conversation_id,sent_at,id)
WHERE status='active' AND wxlocal_ingested_at IS NOT NULL;
