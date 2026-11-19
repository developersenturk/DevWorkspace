CREATE TABLE IF NOT EXISTS installations (
  id TEXT PRIMARY KEY,
  first_seen TEXT NOT NULL,
  last_seen TEXT NOT NULL,
  version TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS installations_last_seen ON installations(last_seen);
