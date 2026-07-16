// Identity/SqlServerIdentityStore.cs
// ----------------------------------
// SQL Server identity/crosswalk store (USE_SQL_SERVER=true). Same shape as the
// SQLite store; tables are prefixed altrata_id_* and scoped by connector_id so
// several connectors can share a database. See docs/SQL_CONTRACT.md.

using Microsoft.Data.SqlClient;

namespace AltrataConnector.Identity;

public sealed class SqlServerIdentityStore : IIdentityStore
{
    private readonly string _connectionString;
    private readonly string _connectorId;
    private readonly object _sync = new();
    private bool _schemaEnsured;

    public SqlServerIdentityStore(string connectionString, string connectorId,
        bool useManagedIdentity = false)
    {
        _connectionString = State.SqlStateStore.BuildConnectionString(connectionString, useManagedIdentity);
        _connectorId = connectorId;
    }

    internal const string SchemaScript = """
        IF OBJECT_ID(N'dbo.altrata_id_seats', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_seats (
            connector_id NVARCHAR(64)  NOT NULL,
            kind         NVARCHAR(32)  NOT NULL,
            value        NVARCHAR(512) NOT NULL,
            CONSTRAINT pk_altrata_id_seats PRIMARY KEY (connector_id, kind, value)
        );
        IF OBJECT_ID(N'dbo.altrata_id_crm_contacts', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_crm_contacts (
            connector_id        NVARCHAR(64)  NOT NULL,
            id                  NVARCHAR(256) NOT NULL,
            email_lower         NVARCHAR(512) NULL,
            name_normalized     NVARCHAR(512) NULL,
            employer_normalized NVARCHAR(512) NULL,
            role_normalized     NVARCHAR(512) NULL,
            CONSTRAINT pk_altrata_id_crm PRIMARY KEY (connector_id, id)
        );
        IF COL_LENGTH(N'dbo.altrata_id_crm_contacts', N'role_normalized') IS NULL
            ALTER TABLE dbo.altrata_id_crm_contacts ADD role_normalized NVARCHAR(512) NULL;
        IF OBJECT_ID(N'dbo.altrata_id_crosswalk', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_crosswalk (
            connector_id   NVARCHAR(64)  NOT NULL,
            altrata_id     NVARCHAR(256) NOT NULL,
            crm_contact_id NVARCHAR(256) NOT NULL,
            match_rule     NVARCHAR(64)  NOT NULL,
            linked_utc     DATETIME2     NOT NULL,
            CONSTRAINT pk_altrata_id_crosswalk PRIMARY KEY (connector_id, altrata_id)
        );
        IF OBJECT_ID(N'dbo.altrata_id_items', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_items (
            connector_id      NVARCHAR(64)  NOT NULL,
            item_id           NVARCHAR(256) NOT NULL,
            dataset           NVARCHAR(64)  NOT NULL,
            acl_hash          NVARCHAR(128) NOT NULL,
            last_ingested_utc DATETIME2     NOT NULL,
            CONSTRAINT pk_altrata_id_items PRIMARY KEY (connector_id, item_id)
        );
        IF OBJECT_ID(N'dbo.altrata_id_path_edges', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_path_edges (
            connector_id   NVARCHAR(64)  NOT NULL,
            person_a       NVARCHAR(256) NOT NULL,
            person_a_name  NVARCHAR(512) NULL,
            person_b       NVARCHAR(256) NOT NULL,
            person_b_name  NVARCHAR(512) NULL,
            strength       FLOAT         NOT NULL,
            intermediaries INT           NOT NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_edges_a')
            CREATE INDEX ix_altrata_id_path_edges_a ON dbo.altrata_id_path_edges (connector_id, person_a);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_edges_b')
            CREATE INDEX ix_altrata_id_path_edges_b ON dbo.altrata_id_path_edges (connector_id, person_b);
        IF OBJECT_ID(N'dbo.altrata_id_path_orgs', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_path_orgs (
            connector_id NVARCHAR(64)  NOT NULL,
            person_id    NVARCHAR(256) NOT NULL,
            org          NVARCHAR(512) NOT NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_orgs')
            CREATE INDEX ix_altrata_id_path_orgs ON dbo.altrata_id_path_orgs (connector_id, person_id);
        IF OBJECT_ID(N'dbo.altrata_id_item_subjects', N'U') IS NULL
        CREATE TABLE dbo.altrata_id_item_subjects (
            connector_id NVARCHAR(64)  NOT NULL,
            item_id      NVARCHAR(256) NOT NULL,
            subject_id   NVARCHAR(256) NOT NULL,
            CONSTRAINT pk_altrata_id_item_subjects PRIMARY KEY (connector_id, item_id, subject_id)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_item_subjects')
            CREATE INDEX ix_altrata_id_item_subjects ON dbo.altrata_id_item_subjects (connector_id, subject_id);
        """;

    private T Execute<T>(Func<SqlConnection, T> operation)
    {
        lock (_sync)
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            if (!_schemaEnsured)
            {
                using var schema = new SqlCommand(SchemaScript, connection);
                schema.ExecuteNonQuery();
                _schemaEnsured = true;
            }
            return operation(connection);
        }
    }

    public void ReplaceSeats(IEnumerable<SeatPrincipal> seats) => Execute<object?>(conn =>
    {
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM dbo.altrata_id_seats WHERE connector_id = @cid",
            ("@cid", _connectorId));
        foreach (var seat in seats)
        {
            Exec(conn, tx,
                "INSERT INTO dbo.altrata_id_seats (connector_id, kind, value) VALUES (@cid, @k, @v)",
                ("@cid", _connectorId), ("@k", seat.Kind.ToString()), ("@v", seat.Value));
        }
        tx.Commit();
        return null;
    });

    public IReadOnlyList<SeatPrincipal> GetSeats() => Execute(conn =>
    {
        var seats = new List<SeatPrincipal>();
        using var cmd = new SqlCommand(
            "SELECT kind, value FROM dbo.altrata_id_seats WHERE connector_id = @cid ORDER BY kind, value", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seats.Add(new SeatPrincipal(Enum.Parse<SeatPrincipalKind>(reader.GetString(0)), reader.GetString(1)));
        return (IReadOnlyList<SeatPrincipal>)seats;
    });

    public void ReplaceCrmContacts(IEnumerable<CrmContact> contacts) => Execute<object?>(conn =>
    {
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM dbo.altrata_id_crm_contacts WHERE connector_id = @cid",
            ("@cid", _connectorId));
        foreach (var contact in contacts)
        {
            Exec(conn, tx, """
                INSERT INTO dbo.altrata_id_crm_contacts
                    (connector_id, id, email_lower, name_normalized, employer_normalized, role_normalized)
                VALUES (@cid, @id, @e, @n, @o, @r)
                """,
                ("@cid", _connectorId), ("@id", contact.Id),
                ("@e", contact.Email?.Trim().ToLowerInvariant()),
                ("@n", EntityNormalizer.NormalizeName(contact.Name)),
                ("@o", EntityNormalizer.NormalizeEmployer(contact.Employer)),
                ("@r", EntityNormalizer.NormalizeName(contact.Role)));
        }
        tx.Commit();
        return null;
    });

    public CrmContact? FindContactByEmail(string emailLower) => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT TOP 1 id, email_lower, name_normalized, employer_normalized, role_normalized " +
            "FROM dbo.altrata_id_crm_contacts WHERE connector_id = @cid AND email_lower = @e", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@e", emailLower);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadContact(reader) : null;
    });

    public CrmContact? FindContactByNameEmployer(string nameNormalized, string employerNormalized) =>
        Execute(conn =>
        {
            using var cmd = new SqlCommand(
                "SELECT TOP 1 id, email_lower, name_normalized, employer_normalized, role_normalized " +
                "FROM dbo.altrata_id_crm_contacts " +
                "WHERE connector_id = @cid AND name_normalized = @n AND employer_normalized = @o", conn);
            cmd.Parameters.AddWithValue("@cid", _connectorId);
            cmd.Parameters.AddWithValue("@n", nameNormalized);
            cmd.Parameters.AddWithValue("@o", employerNormalized);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadContact(reader) : null;
        });

    public IReadOnlyList<CrmContact> ListCrmContacts() => Execute(conn =>
    {
        var contacts = new List<CrmContact>();
        using var cmd = new SqlCommand(
            "SELECT id, email_lower, name_normalized, employer_normalized, role_normalized " +
            "FROM dbo.altrata_id_crm_contacts WHERE connector_id = @cid ORDER BY id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            contacts.Add(ReadContact(reader));
        return (IReadOnlyList<CrmContact>)contacts;
    });

    public int CountCrmContacts() => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_id_crm_contacts WHERE connector_id = @cid", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        return (int)cmd.ExecuteScalar()!;
    });

    private static CrmContact ReadContact(SqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Email = reader.IsDBNull(1) ? null : reader.GetString(1),
        Name = reader.IsDBNull(2) ? null : reader.GetString(2),
        Employer = reader.IsDBNull(3) ? null : reader.GetString(3),
        Role = reader.IsDBNull(4) ? null : reader.GetString(4),
    };

    public void UpsertCrosswalk(CrosswalkEntry entry) => Execute<object?>(conn =>
    {
        Exec(conn, null, """
            MERGE dbo.altrata_id_crosswalk AS target
            USING (SELECT @cid AS connector_id, @a AS altrata_id) AS source
            ON target.connector_id = source.connector_id AND target.altrata_id = source.altrata_id
            WHEN MATCHED THEN UPDATE SET crm_contact_id=@c, match_rule=@m, linked_utc=@u
            WHEN NOT MATCHED THEN INSERT (connector_id, altrata_id, crm_contact_id, match_rule, linked_utc)
            VALUES (@cid, @a, @c, @m, @u);
            """,
            ("@cid", _connectorId), ("@a", entry.AltrataId), ("@c", entry.CrmContactId),
            ("@m", entry.MatchRule), ("@u", entry.LinkedUtc));
        return null;
    });

    public CrosswalkEntry? GetCrosswalk(string altrataId) => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT altrata_id, crm_contact_id, match_rule, linked_utc " +
            "FROM dbo.altrata_id_crosswalk WHERE connector_id = @cid AND altrata_id = @a", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@a", altrataId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new CrosswalkEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3))
            : null;
    });

    public void RemoveCrosswalk(string altrataId) => Execute<object?>(conn =>
    {
        Exec(conn, null,
            "DELETE FROM dbo.altrata_id_crosswalk WHERE connector_id = @cid AND altrata_id = @a",
            ("@cid", _connectorId), ("@a", altrataId));
        return null;
    });

    public IReadOnlyList<CrosswalkEntry> ListCrosswalk() => Execute(conn =>
    {
        var entries = new List<CrosswalkEntry>();
        using var cmd = new SqlCommand(
            "SELECT altrata_id, crm_contact_id, match_rule, linked_utc " +
            "FROM dbo.altrata_id_crosswalk WHERE connector_id = @cid ORDER BY altrata_id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(new CrosswalkEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        return (IReadOnlyList<CrosswalkEntry>)entries;
    });

    public void RecordIngestedItem(IngestedItem item) => Execute<object?>(conn =>
    {
        Exec(conn, null, """
            MERGE dbo.altrata_id_items AS target
            USING (SELECT @cid AS connector_id, @i AS item_id) AS source
            ON target.connector_id = source.connector_id AND target.item_id = source.item_id
            WHEN MATCHED THEN UPDATE SET dataset=@d, acl_hash=@h, last_ingested_utc=@u
            WHEN NOT MATCHED THEN INSERT (connector_id, item_id, dataset, acl_hash, last_ingested_utc)
            VALUES (@cid, @i, @d, @h, @u);
            """,
            ("@cid", _connectorId), ("@i", item.ItemId), ("@d", item.Dataset),
            ("@h", item.AclHash), ("@u", item.LastIngestedUtc));
        return null;
    });

    public void RemoveIngestedItem(string itemId) => Execute<object?>(conn =>
    {
        Exec(conn, null,
            "DELETE FROM dbo.altrata_id_items WHERE connector_id = @cid AND item_id = @i",
            ("@cid", _connectorId), ("@i", itemId));
        Exec(conn, null,
            "DELETE FROM dbo.altrata_id_item_subjects WHERE connector_id = @cid AND item_id = @i",
            ("@cid", _connectorId), ("@i", itemId));
        return null;
    });

    public void RecordItemSubjects(string itemId, IReadOnlyCollection<string> subjectIds) =>
        Execute<object?>(conn =>
        {
            if (subjectIds.Count == 0)
                return null;
            using var tx = conn.BeginTransaction();
            foreach (var subjectId in subjectIds)
                Exec(conn, tx, """
                    IF NOT EXISTS (SELECT 1 FROM dbo.altrata_id_item_subjects
                                   WHERE connector_id = @cid AND item_id = @i AND subject_id = @s)
                        INSERT INTO dbo.altrata_id_item_subjects (connector_id, item_id, subject_id)
                        VALUES (@cid, @i, @s);
                    """,
                    ("@cid", _connectorId), ("@i", itemId), ("@s", subjectId));
            tx.Commit();
            return null;
        });

    public IReadOnlyList<string> ListItemsForSubject(string subjectId) => Execute(conn =>
    {
        var items = new List<string>();
        using var cmd = new SqlCommand(
            "SELECT item_id FROM dbo.altrata_id_item_subjects " +
            "WHERE connector_id = @cid AND subject_id = @s ORDER BY item_id", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        cmd.Parameters.AddWithValue("@s", subjectId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(reader.GetString(0));
        return (IReadOnlyList<string>)items;
    });

    public IReadOnlyList<IngestedItem> ListIngestedItems() => Execute(conn =>
        QueryItems(conn,
            "SELECT item_id, dataset, acl_hash, last_ingested_utc FROM dbo.altrata_id_items " +
            "WHERE connector_id = @cid ORDER BY item_id",
            ("@cid", (object)_connectorId)));

    public IReadOnlyList<IngestedItem> ListItemsWithAclHashOtherThan(string aclHash) => Execute(conn =>
        QueryItems(conn,
            "SELECT item_id, dataset, acl_hash, last_ingested_utc FROM dbo.altrata_id_items " +
            "WHERE connector_id = @cid AND acl_hash <> @h ORDER BY item_id",
            ("@cid", (object)_connectorId), ("@h", (object)aclHash)));

    public int CountIngestedItems() => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_id_items WHERE connector_id = @cid", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        return (int)cmd.ExecuteScalar()!;
    });

    public void ReplacePathIndex(IReadOnlyCollection<PathEdge> edges,
        IReadOnlyCollection<PersonOrg> personOrgs) => Execute<object?>(conn =>
    {
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM dbo.altrata_id_path_edges WHERE connector_id = @cid",
            ("@cid", _connectorId));
        Exec(conn, tx, "DELETE FROM dbo.altrata_id_path_orgs WHERE connector_id = @cid",
            ("@cid", _connectorId));
        foreach (var edge in edges)
            Exec(conn, tx, """
                INSERT INTO dbo.altrata_id_path_edges
                    (connector_id, person_a, person_a_name, person_b, person_b_name, strength, intermediaries)
                VALUES (@cid, @a, @an, @b, @bn, @s, @i)
                """,
                ("@cid", _connectorId), ("@a", edge.PersonA), ("@an", edge.PersonAName),
                ("@b", edge.PersonB), ("@bn", edge.PersonBName),
                ("@s", (object)edge.Strength), ("@i", (object)edge.Intermediaries));
        foreach (var org in personOrgs)
            Exec(conn, tx,
                "INSERT INTO dbo.altrata_id_path_orgs (connector_id, person_id, org) VALUES (@cid, @p, @o)",
                ("@cid", _connectorId), ("@p", org.PersonId), ("@o", org.Org));
        tx.Commit();
        return null;
    });

    public (IReadOnlyList<PathEdge> Edges, IReadOnlyList<PersonOrg> PersonOrgs) LoadPathIndex() =>
        Execute(conn =>
        {
            var edges = new List<PathEdge>();
            using (var cmd = new SqlCommand(
                "SELECT person_a, person_a_name, person_b, person_b_name, strength, intermediaries " +
                "FROM dbo.altrata_id_path_edges WHERE connector_id = @cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", _connectorId);
                using var reader = cmd.ExecuteReader();
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
            using (var cmd = new SqlCommand(
                "SELECT person_id, org FROM dbo.altrata_id_path_orgs WHERE connector_id = @cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", _connectorId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    orgs.Add(new PersonOrg(reader.GetString(0), reader.GetString(1)));
            }
            return ((IReadOnlyList<PathEdge>)edges, (IReadOnlyList<PersonOrg>)orgs);
        });

    public void RemovePersonFromPathIndex(string personId) => Execute<object?>(conn =>
    {
        Exec(conn, null,
            "DELETE FROM dbo.altrata_id_path_edges " +
            "WHERE connector_id = @cid AND (person_a = @p OR person_b = @p)",
            ("@cid", _connectorId), ("@p", personId));
        Exec(conn, null,
            "DELETE FROM dbo.altrata_id_path_orgs WHERE connector_id = @cid AND person_id = @p",
            ("@cid", _connectorId), ("@p", personId));
        return null;
    });

    public int CountPathEdges() => Execute(conn =>
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM dbo.altrata_id_path_edges WHERE connector_id = @cid", conn);
        cmd.Parameters.AddWithValue("@cid", _connectorId);
        return (int)cmd.ExecuteScalar()!;
    });

    public void WipeAll() => Execute<object?>(conn =>
    {
        foreach (var table in new[]
                 {
                     "altrata_id_seats", "altrata_id_crm_contacts", "altrata_id_crosswalk",
                     "altrata_id_items", "altrata_id_path_edges", "altrata_id_path_orgs",
                     "altrata_id_item_subjects",
                 })
        {
            Exec(conn, null, $"DELETE FROM dbo.{table} WHERE connector_id = @cid", ("@cid", _connectorId));
        }
        return null;
    });

    private static IReadOnlyList<IngestedItem> QueryItems(
        SqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        var items = new List<IngestedItem>();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new IngestedItem(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        return items;
    }

    private static void Exec(SqlConnection conn, SqlTransaction? tx, string sql,
        params (string Name, object? Value)[] args)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Connections are opened per operation; nothing persistent to dispose.
    }
}
