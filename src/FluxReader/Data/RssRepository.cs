using System.Globalization;
using FluxReader.Core.Models;
using FluxReader.Models;
using Microsoft.Data.Sqlite;

namespace FluxReader.Data;

public sealed class RssRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RssRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS feeds (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    url                 TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    title               TEXT NOT NULL,
                    site_url            TEXT NOT NULL DEFAULT '',
                    description         TEXT NOT NULL DEFAULT '',
                    created_utc         TEXT NOT NULL,
                    last_refreshed_utc  TEXT NULL,
                    etag                TEXT NULL,
                    last_modified_utc   TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS articles (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    feed_id        INTEGER NOT NULL REFERENCES feeds(id) ON DELETE CASCADE,
                    external_id    TEXT NOT NULL,
                    title          TEXT NOT NULL,
                    link           TEXT NOT NULL DEFAULT '',
                    author         TEXT NOT NULL DEFAULT '',
                    published_utc  TEXT NULL,
                    summary        TEXT NOT NULL DEFAULT '',
                    content        TEXT NOT NULL DEFAULT '',
                    is_read        INTEGER NOT NULL DEFAULT 0,
                    inserted_utc   TEXT NOT NULL,
                    UNIQUE(feed_id, external_id)
                );

                CREATE INDEX IF NOT EXISTS ix_articles_feed_published
                    ON articles(feed_id, published_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_articles_unread
                    ON articles(is_read, published_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Feed>> GetFeedsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.id, f.url, f.title, f.site_url, f.description,
                       f.last_refreshed_utc, f.etag, f.last_modified_utc,
                       SUM(CASE WHEN a.is_read = 0 THEN 1 ELSE 0 END) AS unread_count
                FROM feeds f
                LEFT JOIN articles a ON a.feed_id = f.id
                GROUP BY f.id
                ORDER BY f.title COLLATE NOCASE;
                """;

            var feeds = new List<Feed>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                feeds.Add(new Feed
                {
                    Id = reader.GetInt64(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    SiteUrl = reader.GetString(3),
                    Description = reader.GetString(4),
                    LastRefreshedAt = ReadDate(reader, 5),
                    ETag = ReadNullableString(reader, 6),
                    LastModifiedAt = ReadDate(reader, 7),
                    UnreadCount = reader.GetInt32(8)
                });
            }

            return feeds;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Article>> GetArticlesAsync(
        long? feedId,
        ArticleFilter filter,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.id, a.feed_id, a.external_id, f.title, a.title, a.link,
                       a.author, a.published_utc, a.summary, a.content,
                       a.is_read
                FROM articles a
                INNER JOIN feeds f ON f.id = a.feed_id
                WHERE ($feed_id IS NULL OR a.feed_id = $feed_id)
                  AND ($filter <> 1 OR a.is_read = 0)
                ORDER BY COALESCE(a.published_utc, a.inserted_utc) DESC
                LIMIT 2000;
                """;
            command.Parameters.AddWithValue("$feed_id", feedId is null ? DBNull.Value : feedId.Value);
            command.Parameters.AddWithValue("$filter", (int)filter);

            var articles = new List<Article>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                articles.Add(new Article
                {
                    Id = reader.GetInt64(0),
                    FeedId = reader.GetInt64(1),
                    ExternalId = reader.GetString(2),
                    FeedTitle = reader.GetString(3),
                    Title = reader.GetString(4),
                    Link = reader.GetString(5),
                    Author = reader.GetString(6),
                    PublishedAt = ReadDate(reader, 7),
                    Summary = reader.GetString(8),
                    Content = reader.GetString(9),
                    IsRead = reader.GetBoolean(10)
                });
            }

            return articles;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Feed> AddFeedAsync(
        Uri feedUri,
        ParsedFeed parsedFeed,
        string? etag,
        DateTimeOffset? lastModifiedAt,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO feeds (
                        url, title, site_url, description, created_utc,
                        last_refreshed_utc, etag, last_modified_utc)
                    VALUES (
                        $url, $title, $site_url, $description, $now,
                        $now, $etag, $last_modified)
                    ON CONFLICT(url) DO UPDATE SET
                        title = excluded.title,
                        site_url = excluded.site_url,
                        description = excluded.description,
                        last_refreshed_utc = excluded.last_refreshed_utc,
                        etag = excluded.etag,
                        last_modified_utc = excluded.last_modified_utc;
                    """;
                AddFeedParameters(command, feedUri, parsedFeed, etag, lastModifiedAt);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            long feedId;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM feeds WHERE url = $url COLLATE NOCASE;";
                command.Parameters.AddWithValue("$url", feedUri.AbsoluteUri);
                feedId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                                ?? throw new InvalidOperationException("订阅保存失败。"));
            }

            var feed = new Feed
            {
                Id = feedId,
                Url = feedUri.AbsoluteUri,
                Title = parsedFeed.Title,
                SiteUrl = parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty,
                Description = parsedFeed.Description,
                LastRefreshedAt = DateTimeOffset.UtcNow,
                ETag = etag,
                LastModifiedAt = lastModifiedAt
            };
            var inserted = await UpsertArticlesCoreAsync(connection, feed.Id, parsedFeed.Articles, cancellationToken);
            feed.UnreadCount = inserted.Count;
            return feed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> UpdateFeedAsync(
        Feed feed,
        ParsedFeed parsedFeed,
        string? etag,
        DateTimeOffset? lastModifiedAt,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE feeds SET
                        title = $title,
                        site_url = $site_url,
                        description = $description,
                        last_refreshed_utc = $now,
                        etag = $etag,
                        last_modified_utc = $last_modified
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", feed.Id);
                command.Parameters.AddWithValue("$title", parsedFeed.Title);
                command.Parameters.AddWithValue("$site_url", parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty);
                command.Parameters.AddWithValue("$description", parsedFeed.Description);
                command.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
                command.Parameters.AddWithValue("$last_modified", FormatNullableDate(lastModifiedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var inserted = await UpsertArticlesCoreAsync(
                connection,
                feed.Id,
                parsedFeed.Articles,
                cancellationToken,
                (SqliteTransaction)transaction);
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TouchFeedAsync(long feedId, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "UPDATE feeds SET last_refreshed_utc = $now WHERE id = $id;",
            command =>
            {
                command.Parameters.AddWithValue("$id", feedId);
                command.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
            },
            cancellationToken);
    }

    public Task SetArticleReadAsync(long articleId, bool isRead, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE articles SET is_read = $value WHERE id = $id;",
            command =>
            {
                command.Parameters.AddWithValue("$id", articleId);
                command.Parameters.AddWithValue("$value", isRead);
            },
            cancellationToken);

    public Task MarkAllReadAsync(long? feedId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE articles SET is_read = 1 WHERE $feed_id IS NULL OR feed_id = $feed_id;",
            command => command.Parameters.AddWithValue("$feed_id", feedId is null ? DBNull.Value : feedId.Value),
            cancellationToken);

    public Task DeleteFeedAsync(long feedId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "DELETE FROM feeds WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", feedId),
            cancellationToken);

    private async Task ExecuteAsync(
        string sql,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> UpsertArticlesCoreAsync(
        SqliteConnection connection,
        long feedId,
        IReadOnlyList<ParsedArticle> articles,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var insertedTitles = new List<string>();
        foreach (var article in articles)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO articles (
                    feed_id, external_id, title, link, author, published_utc,
                    summary, content, inserted_utc)
                VALUES (
                    $feed_id, $external_id, $title, $link, $author, $published,
                    $summary, $content, $inserted)
                ON CONFLICT(feed_id, external_id) DO NOTHING
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$feed_id", feedId);
            command.Parameters.AddWithValue("$external_id", article.ExternalId);
            command.Parameters.AddWithValue("$title", article.Title);
            command.Parameters.AddWithValue("$link", article.Link?.AbsoluteUri ?? string.Empty);
            command.Parameters.AddWithValue("$author", article.Author);
            command.Parameters.AddWithValue("$published", FormatNullableDate(article.PublishedAt));
            command.Parameters.AddWithValue("$summary", article.Summary);
            command.Parameters.AddWithValue("$content", article.Content);
            command.Parameters.AddWithValue("$inserted", FormatDate(DateTimeOffset.UtcNow));

            if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            {
                insertedTitles.Add(article.Title);
            }
        }

        return insertedTitles;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddFeedParameters(
        SqliteCommand command,
        Uri feedUri,
        ParsedFeed parsedFeed,
        string? etag,
        DateTimeOffset? lastModifiedAt)
    {
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$url", feedUri.AbsoluteUri);
        command.Parameters.AddWithValue("$title", parsedFeed.Title);
        command.Parameters.AddWithValue("$site_url", parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty);
        command.Parameters.AddWithValue("$description", parsedFeed.Description);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_modified", FormatNullableDate(lastModifiedAt));
    }

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableDate(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatDate(value.Value);

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
