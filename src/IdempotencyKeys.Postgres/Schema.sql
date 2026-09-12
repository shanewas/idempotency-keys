CREATE TABLE IF NOT EXISTS idempotency_keys (
  key TEXT NOT NULL,
  scope TEXT NOT NULL,
  fingerprint TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'inflight',
  response_status INT,
  response_headers JSONB,
  response_body BYTEA,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (scope, key)
);
CREATE INDEX IF NOT EXISTS ix_idempotency_keys_created ON idempotency_keys (created_at);
