-- Nexus migration 10 → 11 (OpenRouter as a second external provider, and the cost it reports in US$).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 10.
-- New databases get the same table directly from schema.sql + security.sql.
-- A CHECK cannot be altered in SQLite, so llm_usage is rebuilt: the rows are copied as they are, and dropping the old
-- table also drops its index and its append-only triggers, which are created again below (same text as security.sql).

CREATE TABLE llm_usage_v11 (
    id                INTEGER PRIMARY KEY,
    occurred_at       TEXT NOT NULL,
    user_id           INTEGER REFERENCES users (id),
    warehouse_id      INTEGER REFERENCES warehouses (id),
    role              TEXT NOT NULL CHECK (role IN ('planner', 'matcher')),
    provider          TEXT NOT NULL CHECK (provider IN ('deepseek', 'openrouter', 'local')),
    model             TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 80),
    calls             INTEGER NOT NULL CHECK (calls >= 0),
    prompt_tokens     INTEGER NOT NULL CHECK (prompt_tokens >= 0),
    completion_tokens INTEGER NOT NULL CHECK (completion_tokens >= 0),
    result            TEXT NOT NULL CHECK (result IN ('answer', 'proposals', 'recipe', 'no_match', 'escalated', 'failed')),
    cache_read_tokens  INTEGER NOT NULL DEFAULT 0 CHECK (cache_read_tokens >= 0),
    cache_write_tokens INTEGER NOT NULL DEFAULT 0 CHECK (cache_write_tokens >= 0),
    cost_usd          REAL CHECK (cost_usd IS NULL OR cost_usd >= 0)
) STRICT;

INSERT INTO llm_usage_v11 (id, occurred_at, user_id, warehouse_id, role, provider, model, calls, prompt_tokens,
                           completion_tokens, result, cache_read_tokens, cache_write_tokens)
SELECT id, occurred_at, user_id, warehouse_id, role, provider, model, calls, prompt_tokens,
       completion_tokens, result, cache_read_tokens, cache_write_tokens
  FROM llm_usage;

DROP TABLE llm_usage;
ALTER TABLE llm_usage_v11 RENAME TO llm_usage;

CREATE INDEX llm_usage_by_time ON llm_usage (occurred_at);

CREATE TRIGGER llm_usage_deny_update BEFORE UPDATE ON llm_usage BEGIN
    SELECT RAISE(ABORT, 'DENY: llm_usage is append-only');
END;

CREATE TRIGGER llm_usage_deny_delete BEFORE DELETE ON llm_usage BEGIN
    SELECT RAISE(ABORT, 'DENY: llm_usage is append-only');
END;
