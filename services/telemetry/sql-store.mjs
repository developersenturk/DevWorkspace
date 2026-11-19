// Both adapters expose run(sql, ...params) and all(sql, ...params).
export function createStore(db) {
  return {
    async record(id, version, timestamp) {
      await db.run(`INSERT INTO installations(id, first_seen, last_seen, version) VALUES(?, ?, ?, ?)
        ON CONFLICT(id) DO UPDATE SET last_seen = excluded.last_seen, version = excluded.version
        WHERE installations.last_seen < excluded.last_seen`, id, timestamp, timestamp, version);
    },
    async summary(now) {
      const since = days => new Date(now.getTime() - days * 86400000).toISOString();
      const [counts] = await db.all(`SELECT COUNT(*) AS totalInstallations,
        COALESCE(SUM(last_seen >= ?), 0) AS active24Hours,
        COALESCE(SUM(last_seen >= ?), 0) AS active7Days,
        COALESCE(SUM(last_seen >= ?), 0) AS active30Days FROM installations`, since(1), since(7), since(30));
      const versions = await db.all('SELECT version, COUNT(*) AS installations FROM installations GROUP BY version ORDER BY installations DESC, version');
      return { ...counts, versions };
    }
  };
}
