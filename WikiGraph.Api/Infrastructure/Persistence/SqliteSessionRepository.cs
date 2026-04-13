using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WikiGraph.Contracts;

namespace WikiGraph.Api.Infrastructure.Persistence;

public sealed class SqliteSessionRepository
{
    private const string DefaultSessionTitle = "New session";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISqliteConnectionFactory _connectionFactory;

    // Creates the repository that reads and writes session data in SQLite.
    public SqliteSessionRepository(ISqliteConnectionFactory connectionFactory, SessionMemoryDb _)
    {
        _connectionFactory = connectionFactory;
    }

    // Inserts a new session row and returns the stored summary.
    public SessionSummary CreateSession(string title)
    {
        var nowUtc = DateTime.UtcNow;
        var session = new SessionSummary(Guid.NewGuid().ToString("N"), title, nowUtc, nowUtc);

        using var connection = _connectionFactory.OpenConnection();
        InsertSession(connection, session, SessionMemoryDb.DefaultUserId, null);
        return session;
    }

    // Ensures a session exists, or updates its title and access time.
    public void EnsureSession(string sessionId, string title, DateTime accessedUtc)
    {
        using var connection = _connectionFactory.OpenConnection();
        var existingSession = LoadSession(connection, sessionId);
        if (existingSession is null)
        {
            InsertSession(
                connection,
                new SessionSummary(sessionId, title, accessedUtc, accessedUtc),
                SessionMemoryDb.DefaultUserId,
                null);
            return;
        }

        using var transaction = connection.BeginTransaction();
        UpdateSession(connection, sessionId, ResolveSessionTitle(existingSession.Title, title), accessedUtc, transaction);
        transaction.Commit();
    }

    // Returns the current session list ordered by recent activity.
    public IReadOnlyList<SessionSummary> GetSessions()
    {
        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SessionId, Title, CreatedUtc, LastAccessUtc
            FROM Sessions
            WHERE UserId = $userId
            ORDER BY LastAccessUtc DESC;
            """;
        command.Parameters.AddWithValue("$userId", SessionMemoryDb.DefaultUserId);

        using var reader = command.ExecuteReader();
        var sessions = new List<SessionSummary>();
        while (reader.Read())
        {
            sessions.Add(ReadSessionSummary(reader));
        }

        return sessions;
    }

    // Returns one session with its messages, citations, and graphs.
    public SessionDetailDto? GetSession(string sessionId)
    {
        using var connection = _connectionFactory.OpenConnection();
        var session = LoadSession(connection, sessionId);
        if (session is null)
        {
            return null;
        }

        return new SessionDetailDto(
            session,
            LoadMessages(connection, sessionId),
            LoadCitations(connection, sessionId),
            LoadGraphs(connection, sessionId));
    }

    // Returns just the graphs when the session exists.
    public IReadOnlyList<GraphDto>? GetGraphs(string sessionId)
    {
        using var connection = _connectionFactory.OpenConnection();
        return LoadSession(connection, sessionId) is null ? null : LoadGraphs(connection, sessionId);
    }

    // Saves one user turn and one assistant turn plus their citations and graphs.
    public void SaveTurn(
        string sessionId,
        string sessionTitle,
        MessageDto userMessage,
        MessageDto assistantMessage,
        IReadOnlyList<CitationDto> citations,
        IReadOnlyList<GraphDto> graphs)
    {
        using var connection = _connectionFactory.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var existingSession = LoadSession(connection, sessionId);
        if (existingSession is null)
        {
            InsertSession(
                connection,
                new SessionSummary(sessionId, sessionTitle, userMessage.CreatedUtc, assistantMessage.CreatedUtc),
                SessionMemoryDb.DefaultUserId,
                transaction);
        }
        else
        {
            UpdateSession(connection, sessionId, ResolveSessionTitle(existingSession.Title, sessionTitle), assistantMessage.CreatedUtc, transaction);
        }

        InsertMessage(connection, sessionId, userMessage, transaction);
        var assistantMessageId = InsertMessage(connection, sessionId, assistantMessage, transaction);

        foreach (var citation in citations)
        {
            InsertCitation(connection, sessionId, assistantMessageId, citation, transaction);
        }

        foreach (var graph in graphs)
        {
            InsertGraph(connection, sessionId, graph, transaction);
        }

        transaction.Commit();
    }

    // Keeps the original title unless the session still has the default placeholder title.
    private static string ResolveSessionTitle(string currentTitle, string requestedTitle) =>
        string.Equals(currentTitle, DefaultSessionTitle, StringComparison.OrdinalIgnoreCase)
            ? requestedTitle
            : currentTitle;

    // Inserts a session row into the database.
    private static void InsertSession(SqliteConnection connection, SessionSummary session, string userId, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Sessions (SessionId, UserId, Title, CreatedUtc, LastAccessUtc)
            VALUES ($sessionId, $userId, $title, $createdUtc, $lastAccessUtc);
            """;
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$createdUtc", session.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$lastAccessUtc", session.LastAccessUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    // Updates the title and last access time for an existing session.
    private static void UpdateSession(SqliteConnection connection, string sessionId, string title, DateTime lastAccessUtc, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Sessions
            SET Title = $title, LastAccessUtc = $lastAccessUtc
            WHERE SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$lastAccessUtc", lastAccessUtc.ToString("O"));
        command.ExecuteNonQuery();
    }
   public void DeleteSession(string sessionId)
    {
        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.Transaction = null;
        command.CommandText = """
            DELETE FROM Sessions WHERE SessionId = $sessionId;

            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.ExecuteNonQuery();
    }


    // Inserts one message and returns its generated row id.
    private static long InsertMessage(SqliteConnection connection, string sessionId, MessageDto message, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Messages (SessionId, Role, Content, CreatedUtc)
            VALUES ($sessionId, $role, $content, $createdUtc);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$createdUtc", message.CreatedUtc.ToString("O"));
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return (long)(idCommand.ExecuteScalar() ?? 0L);
    }

    // Inserts one citation linked to the assistant message that produced it.
    private static void InsertCitation(SqliteConnection connection, string sessionId, long assistantMessageId, CitationDto citation, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Citations (SessionId, MessageId, Title, Url, Section, ChunkId)
            VALUES ($sessionId, $messageId, $title, $url, $section, $chunkId);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$messageId", assistantMessageId);
        command.Parameters.AddWithValue("$title", citation.Title);
        command.Parameters.AddWithValue("$url", citation.Url);
        command.Parameters.AddWithValue("$section", (object?)citation.Section ?? DBNull.Value);
        command.Parameters.AddWithValue("$chunkId", (object?)citation.ChunkId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // Serializes one graph row into JSON columns.
    private static void InsertGraph(SqliteConnection connection, string sessionId, GraphDto graph, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Graphs (SessionId, Topic, NodesJson, EdgesJson)
            VALUES ($sessionId, $topic, $nodesJson, $edgesJson);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$topic", graph.Topic);
        command.Parameters.AddWithValue("$nodesJson", JsonSerializer.Serialize(graph.Nodes, JsonOptions));
        command.Parameters.AddWithValue("$edgesJson", JsonSerializer.Serialize(graph.Edges, JsonOptions));
        command.ExecuteNonQuery();
    }

    // Loads a session header row when it exists.
    private static SessionSummary? LoadSession(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SessionId, Title, CreatedUtc, LastAccessUtc
            FROM Sessions
            WHERE SessionId = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSessionSummary(reader) : null;
    }

    // Loads the ordered chat messages for one session.
    private static IReadOnlyList<MessageDto> LoadMessages(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Role, Content, CreatedUtc
            FROM Messages
            WHERE SessionId = $sessionId
            ORDER BY MessageId ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = command.ExecuteReader();
        var messages = new List<MessageDto>();
        while (reader.Read())
        {
            messages.Add(new MessageDto(
                reader.GetString(0),
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind)));
        }

        return messages;
    }

    // Loads the citations associated with one session.
    private static IReadOnlyList<CitationDto> LoadCitations(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Title, Url, Section, ChunkId
            FROM Citations
            WHERE SessionId = $sessionId
            ORDER BY COALESCE(MessageId, 0) ASC, CitationId ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = command.ExecuteReader();
        var citations = new List<CitationDto>();
        while (reader.Read())
        {
            citations.Add(new CitationDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return citations;
    }

    // Loads graph rows and deserializes the node and edge JSON.
    private static IReadOnlyList<GraphDto> LoadGraphs(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Topic, NodesJson, EdgesJson
            FROM Graphs
            WHERE SessionId = $sessionId
            ORDER BY GraphId ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = command.ExecuteReader();
        var graphs = new List<GraphDto>();
        while (reader.Read())
        {
            var nodes = JsonSerializer.Deserialize<List<GraphNodeDto>>(reader.GetString(1), JsonOptions) ?? [];
            var edges = JsonSerializer.Deserialize<List<GraphEdgeDto>>(reader.GetString(2), JsonOptions) ?? [];
            graphs.Add(new GraphDto(reader.GetString(0), nodes, edges));
        }

        return graphs;
    }

    // Maps a SQLite reader row back into a session summary DTO.
    private static SessionSummary ReadSessionSummary(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            DateTime.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind));
}
