// Identity/SqliteIdentityStore.cs
// -------------------------------
// Default identity/crosswalk store: SQLite at data/{CONNECTOR_ID}_identity.db.

using Microsoft.Data.Sqlite;

namespace AltrataConnector.Identity;

public sealed class SqliteIdentityStore : IIdentityStore
{
    private readonly SqliteConnection _connection;
    private readonly object _sync = new();

    public string DatabasePath { get; }

    public SqliteIdentityStore(string connectorId, string? dataDir = null)
        : this(ResolvePath(connectorId, dataDir))
    {
    }

    /// <summary>Open (or create) the store at an explicit path. ":memory:" supported for tests.</summary>
    public SqliteIdentityStore(string databasePath)
    {
        DatabasePath = databasePath;
        if (databasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        // Contended writes must wait, not fail. Writes here are autocommit
        // statements, so under the default rollback journal each takes a SHARED
        // lock then upgrades to RESERVED; when another connection holds RESERVED,
        // SQLite returns SQLITE_BUSY on that upgrade immediately and deliberately
        // WITHOUT consulting the busy handler, because blocking mid-upgrade would
        // deadlock. busy_timeout alone therefore cannot fix it (measured on
        // Windows: it took a multi-node storm from 4 failing writers to 3). WAL
        // removes the upgrade - readers never block the writer - and the
        // write-write contention that remains does honour the busy handler.
        //
        // Windows enforces the locking that exposes this; POSIX hides it, so this
        // only ever failed on Windows Server, the deployment target. journal_mode
        // is persisted in the database file, so the pragma is a no-op after the
        // first connection and upgrades a file made by an older build.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema();
    }

    private static string ResolvePath(string connectorId, string? dataDir)
    {
        var dir = dataDir
            ?? Environment.GetEnvironmentVariable("DATA_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        return Path.Combine(dir, $"{connectorId}_identity.db");
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS seats (
                kind  TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (kind, value)
            );
            CREATE TABLE IF NOT EXISTS crm_contacts (
                id                  TEXT PRIMARY KEY,
                email_lower         TEXT,
                name_normalized     TEXT,
                employer_normalized TEXT,
                role_normalized     TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_crm_contacts_email ON crm_contacts (email_lower);
            CREATE INDEX IF NOT EXISTS ix_crm_contacts_name  ON crm_contacts (name_normalized, employer_normalized);
            CREATE TABLE IF NOT EXISTS crosswalk (
                altrata_id     TEXT PRIMARY KEY,
                crm_contact_id TEXT NOT NULL,
                match_rule     TEXT NOT NULL,
                linked_utc     TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ingested_items (
                item_id           TEXT PRIMARY KEY,
                dataset           TEXT NOT NULL,
                acl_hash          TEXT NOT NULL,
                last_ingested_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS path_edges (
                person_a      TEXT NOT NULL,
                person_a_name TEXT,
                person_b      TEXT NOT NULL,
                person_b_name TEXT,
                strength      REAL NOT NULL,
                intermediaries INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_path_edges_a ON path_edges (person_a);
            CREATE INDEX IF NOT EXISTS ix_path_edges_b ON path_edges (person_b);
            CREATE TABLE IF NOT EXISTS path_person_orgs (
                person_id TEXT NOT NULL,
                org       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_path_person_orgs ON path_person_orgs (person_id);
            CREATE TABLE IF NOT EXISTS item_subjects (
                item_id    TEXT NOT NULL,
                subject_id TEXT NOT NULL,
                PRIMARY KEY (item_id, subject_id)
            );
            CREATE INDEX IF NOT EXISTS ix_item_subjects_subject ON item_subjects (subject_id);
            """);

        // v2 migration: role_normalized added for the fuzzy matching tier.
        // Pre-existing databases lack the column; new ones get it via CREATE.
        try
        {
            Exec("ALTER TABLE crm_contacts ADD COLUMN role_normalized TEXT");
        }
        catch (SqliteException)
        {
            // duplicate column — schema already current
        }
    }

    private void Exec(string sql, params (string Name, object? Value)[] args) =>
        ExecTx(sql, null, args);

    private void ExecTx(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // ---- seats -----------------------------------------------------------------

    public void ReplaceSeats(IEnumerable<SeatPrincipal> seats)
    {
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            ExecTx("DELETE FROM seats", tx);
            foreach (var seat in seats)
                ExecTx("INSERT OR REPLACE INTO seats (kind, value) VALUES (@k, @v)", tx,
                    ("@k", seat.Kind.ToString()), ("@v", seat.Value));
            tx.Commit();
        }
    }

    public IReadOnlyList<SeatPrincipal> GetSeats()
    {
        lock (_sync)
        {
            var seats = new List<SeatPrincipal>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT kind, value FROM seats ORDER BY kind, value";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                seats.Add(new SeatPrincipal(
                    Enum.Parse<SeatPrincipalKind>(reader.GetString(0)), reader.GetString(1)));
            return seats;
        }
    }

    // ---- CRM contacts --------------------------------------------------------------

    public void ReplaceCrmContacts(IEnumerable<CrmContact> contacts)
    {
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            ExecTx("DELETE FROM crm_contacts", tx);
            foreach (var contact in contacts)
            {
                ExecTx("""
                    INSERT OR REPLACE INTO crm_contacts (id, email_lower, name_normalized, employer_normalized, role_normalized)
                    VALUES (@id, @e, @n, @o, @r)
                    """, tx,
                    ("@id", contact.Id),
                    ("@e", contact.Email?.Trim().ToLowerInvariant()),
                    ("@n", EntityNormalizer.NormalizeName(contact.Name)),
                    ("@o", EntityNormalizer.NormalizeEmployer(contact.Employer)),
                    ("@r", EntityNormalizer.NormalizeName(contact.Role)));
            }
            tx.Commit();
        }
    }

    public CrmContact? FindContactByEmail(string emailLower)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT id, email_lower, name_normalized, employer_normalized, role_normalized " +
                "FROM crm_contacts WHERE email_lower = @e LIMIT 1";
            command.Parameters.AddWithValue("@e", emailLower);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadContact(reader) : null;
        }
    }

    public CrmContact? FindContactByNameEmployer(string nameNormalized, string employerNormalized)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT id, email_lower, name_normalized, employer_normalized, role_normalized " +
                "FROM crm_contacts WHERE name_normalized = @n AND employer_normalized = @o LIMIT 1";
            command.Parameters.AddWithValue("@n", nameNormalized);
            command.Parameters.AddWithValue("@o", employerNormalized);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadContact(reader) : null;
        }
    }

    public IReadOnlyList<CrmContact> ListCrmContacts()
    {
        lock (_sync)
        {
            var contacts = new List<CrmContact>();
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT id, email_lower, name_normalized, employer_normalized, role_normalized " +
                "FROM crm_contacts ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                contacts.Add(ReadContact(reader));
            return contacts;
        }
    }

    public int CountCrmContacts()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM crm_contacts";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static CrmContact ReadContact(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Email = reader.IsDBNull(1) ? null : reader.GetString(1),
        Name = reader.IsDBNull(2) ? null : reader.GetString(2),
        Employer = reader.IsDBNull(3) ? null : reader.GetString(3),
        Role = reader.IsDBNull(4) ? null : reader.GetString(4),
    };

    // ---- crosswalk --------------------------------------------------------------------

    public void UpsertCrosswalk(CrosswalkEntry entry)
    {
        lock (_sync)
        {
            Exec("""
                INSERT OR REPLACE INTO crosswalk (altrata_id, crm_contact_id, match_rule, linked_utc)
                VALUES (@a, @c, @m, @u)
                """,
                ("@a", entry.AltrataId), ("@c", entry.CrmContactId),
                ("@m", entry.MatchRule), ("@u", entry.LinkedUtc.ToString("o")));
        }
    }

    public CrosswalkEntry? GetCrosswalk(string altrataId)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT altrata_id, crm_contact_id, match_rule, linked_utc FROM crosswalk WHERE altrata_id = @a";
            command.Parameters.AddWithValue("@a", altrataId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadCrosswalk(reader) : null;
        }
    }

    public void RemoveCrosswalk(string altrataId)
    {
        lock (_sync)
            Exec("DELETE FROM crosswalk WHERE altrata_id = @a", ("@a", altrataId));
    }

    public IReadOnlyList<CrosswalkEntry> ListCrosswalk()
    {
        lock (_sync)
        {
            var entries = new List<CrosswalkEntry>();
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT altrata_id, crm_contact_id, match_rule, linked_utc FROM crosswalk ORDER BY altrata_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                entries.Add(ReadCrosswalk(reader));
            return entries;
        }
    }

    private static CrosswalkEntry ReadCrosswalk(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        DateTime.Parse(reader.GetString(3), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));

    // ---- ingested items ---------------------------------------------------------------------

    public void RecordIngestedItem(IngestedItem item)
    {
        lock (_sync)
        {
            Exec("""
                INSERT OR REPLACE INTO ingested_items (item_id, dataset, acl_hash, last_ingested_utc)
                VALUES (@i, @d, @h, @u)
                """,
                ("@i", item.ItemId), ("@d", item.Dataset),
                ("@h", item.AclHash), ("@u", item.LastIngestedUtc.ToString("o")));
        }
    }

    public bool TryUpdateAclHash(string itemId, string aclHash, DateTime lastIngestedUtc)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "UPDATE ingested_items SET acl_hash = @h, last_ingested_utc = @u WHERE item_id = @i";
            command.Parameters.AddWithValue("@h", aclHash);
            command.Parameters.AddWithValue("@u", lastIngestedUtc.ToString("o"));
            command.Parameters.AddWithValue("@i", itemId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void RemoveIngestedItem(string itemId)
    {
        lock (_sync)
        {
            Exec("DELETE FROM ingested_items WHERE item_id = @i", ("@i", itemId));
            Exec("DELETE FROM item_subjects WHERE item_id = @i", ("@i", itemId));
        }
    }

    public void RecordItemSubjects(string itemId, IReadOnlyCollection<string> subjectIds)
    {
        if (subjectIds.Count == 0)
            return;
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var subjectId in subjectIds)
                ExecTx("INSERT OR IGNORE INTO item_subjects (item_id, subject_id) VALUES (@i, @s)", tx,
                    ("@i", itemId), ("@s", subjectId));
            tx.Commit();
        }
    }

    public IReadOnlyList<string> ListItemsForSubject(string subjectId)
    {
        lock (_sync)
        {
            var items = new List<string>();
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT item_id FROM item_subjects WHERE subject_id = @s ORDER BY item_id";
            command.Parameters.AddWithValue("@s", subjectId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(reader.GetString(0));
            return items;
        }
    }

    public IReadOnlyList<IngestedItem> ListIngestedItems() =>
        QueryItems("SELECT item_id, dataset, acl_hash, last_ingested_utc FROM ingested_items ORDER BY item_id");

    public IReadOnlyList<IngestedItem> ListItemsWithAclHashOtherThan(string aclHash)
    {
        lock (_sync)
        {
            var items = new List<IngestedItem>();
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT item_id, dataset, acl_hash, last_ingested_utc FROM ingested_items " +
                "WHERE acl_hash <> @h ORDER BY item_id";
            command.Parameters.AddWithValue("@h", aclHash);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(ReadItem(reader));
            return items;
        }
    }

    public int CountIngestedItems()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM ingested_items";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private IReadOnlyList<IngestedItem> QueryItems(string sql)
    {
        lock (_sync)
        {
            var items = new List<IngestedItem>();
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(ReadItem(reader));
            return items;
        }
    }

    private static IngestedItem ReadItem(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        DateTime.Parse(reader.GetString(3), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));

    // ---- relationship path index ------------------------------------------------------------------

    public void ReplacePathIndex(IReadOnlyCollection<PathEdge> edges, IReadOnlyCollection<PersonOrg> personOrgs)
    {
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            ExecTx("DELETE FROM path_edges; DELETE FROM path_person_orgs;", tx);
            foreach (var edge in edges)
                ExecTx("""
                    INSERT INTO path_edges
                        (person_a, person_a_name, person_b, person_b_name, strength, intermediaries)
                    VALUES (@a, @an, @b, @bn, @s, @i)
                    """, tx,
                    ("@a", edge.PersonA), ("@an", edge.PersonAName),
                    ("@b", edge.PersonB), ("@bn", edge.PersonBName),
                    ("@s", (object)edge.Strength), ("@i", (object)edge.Intermediaries));
            foreach (var org in personOrgs)
                ExecTx("INSERT INTO path_person_orgs (person_id, org) VALUES (@p, @o)", tx,
                    ("@p", org.PersonId), ("@o", org.Org));
            tx.Commit();
        }
    }

    public (IReadOnlyList<PathEdge> Edges, IReadOnlyList<PersonOrg> PersonOrgs) LoadPathIndex()
    {
        lock (_sync)
        {
            var edges = new List<PathEdge>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT person_a, person_a_name, person_b, person_b_name, strength, intermediaries " +
                    "FROM path_edges ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    edges.Add(new PathEdge(
                        PersonA: reader.GetString(0),
                        PersonB: reader.GetString(2),
                        Strength: reader.GetDouble(4),
                        Intermediaries: reader.GetInt32(5),
                        PersonAName: reader.IsDBNull(1) ? null : reader.GetString(1),
                        PersonBName: reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            var orgs = new List<PersonOrg>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT person_id, org FROM path_person_orgs ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    orgs.Add(new PersonOrg(reader.GetString(0), reader.GetString(1)));
            }
            return (edges, orgs);
        }
    }

    public void RemovePersonFromPathIndex(string personId)
    {
        lock (_sync)
        {
            Exec("DELETE FROM path_edges WHERE person_a = @p OR person_b = @p", ("@p", personId));
            Exec("DELETE FROM path_person_orgs WHERE person_id = @p", ("@p", personId));
        }
    }

    public int CountPathEdges()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM path_edges";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    // ---- purge -----------------------------------------------------------------------------------

    public void WipeAll()
    {
        lock (_sync)
            Exec("DELETE FROM seats; DELETE FROM crm_contacts; DELETE FROM crosswalk; " +
                 "DELETE FROM ingested_items; DELETE FROM path_edges; DELETE FROM path_person_orgs; " +
                 "DELETE FROM item_subjects;");
    }

    public void Dispose() => _connection.Dispose();
}
