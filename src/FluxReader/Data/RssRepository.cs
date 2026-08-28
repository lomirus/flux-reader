using System.Globalization;
using FluxReader.Core.Models;
using FluxReader.Core.Services;
using FluxReader.Models;
using FluxReader.Services;
using Microsoft.Data.Sqlite;

namespace FluxReader.Data;

public sealed class RssRepository
{
    private readonly string _connectionString;
    private readonly LocalizationService _localization;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RssRepository(string databasePath, LocalizationService localization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _localization = localization;
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

                CREATE TABLE IF NOT EXISTS feed_groups (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    name        TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    created_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS feeds (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    url                 TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    title               TEXT NOT NULL,
                    site_url            TEXT NOT NULL DEFAULT '',
                    icon_url            TEXT NOT NULL DEFAULT '',
                    description         TEXT NOT NULL DEFAULT '',
                    group_id            INTEGER NULL REFERENCES feed_groups(id) ON DELETE SET NULL,
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
                CREATE INDEX IF NOT EXISTS ix_feeds_group
                    ON feeds(group_id, title COLLATE NOCASE);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await MigrateArticleContentToHtmlAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FeedGroup>> GetFeedGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name
                FROM feed_groups
                ORDER BY name COLLATE NOCASE;
                """;

            var groups = new List<FeedGroup>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                groups.Add(new FeedGroup
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1)
                });
            }

            return groups;
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
                SELECT f.id, f.url, f.title, f.site_url, f.icon_url, f.description,
                       f.group_id, f.last_refreshed_utc, f.etag, f.last_modified_utc,
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
                    IconUrl = reader.GetString(4),
                    Description = reader.GetString(5),
                    GroupId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    LastRefreshedAt = ReadDate(reader, 7),
                    ETag = ReadNullableString(reader, 8),
                    LastModifiedAt = ReadDate(reader, 9),
                    UnreadCount = reader.GetInt32(10)
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
        IReadOnlyCollection<long>? feedIds,
        long? groupId,
        ArticleFilter filter,
        string? searchQuery,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var normalizedFeedIds = feedIds?.Distinct().ToArray() ?? [];
            var feedFilter = string.Empty;
            if (normalizedFeedIds.Length > 0)
            {
                var parameterNames = new string[normalizedFeedIds.Length];
                for (var index = 0; index < normalizedFeedIds.Length; index++)
                {
                    var parameterName = $"$feed_id_{index}";
                    parameterNames[index] = parameterName;
                    command.Parameters.AddWithValue(parameterName, normalizedFeedIds[index]);
                }

                feedFilter = $"AND a.feed_id IN ({string.Join(", ", parameterNames)})";
            }

            command.CommandText = $"""
                SELECT a.id, a.feed_id, a.external_id, f.title, a.title, a.link,
                       a.author, a.published_utc, a.summary, a.content,
                       a.is_read
                FROM articles a
                INNER JOIN feeds f ON f.id = a.feed_id
                WHERE 1 = 1
                  {feedFilter}
                  AND ($group_id IS NULL OR f.group_id = $group_id)
                  AND ($filter <> 1 OR a.is_read = 0)
                  AND ($search_query IS NULL OR
                       article_search_rank(a.title, a.summary, a.content, $search_query) >= 0)
                ORDER BY CASE WHEN $search_query IS NULL THEN 0
                              ELSE article_search_rank(
                                  a.title, a.summary, a.content, $search_query)
                         END,
                         COALESCE(a.published_utc, a.inserted_utc) DESC,
                         a.id DESC
                LIMIT 2000;
                """;
            command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
            command.Parameters.AddWithValue("$filter", (int)filter);
            command.Parameters.AddWithValue(
                "$search_query",
                string.IsNullOrWhiteSpace(searchQuery) ? DBNull.Value : searchQuery.Trim());

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
        long? groupId,
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
                        url, title, site_url, icon_url, description, created_utc,
                        last_refreshed_utc, etag, last_modified_utc, group_id)
                    VALUES (
                        $url, $title, $site_url, $icon_url, $description, $now,
                        $now, $etag, $last_modified, $group_id)
                    ON CONFLICT(url) DO UPDATE SET
                        title = excluded.title,
                        site_url = excluded.site_url,
                        icon_url = excluded.icon_url,
                        description = excluded.description,
                        last_refreshed_utc = excluded.last_refreshed_utc,
                        etag = excluded.etag,
                        last_modified_utc = excluded.last_modified_utc,
                        group_id = excluded.group_id;
                    """;
                AddFeedParameters(command, feedUri, parsedFeed, etag, lastModifiedAt, groupId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            long feedId;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM feeds WHERE url = $url COLLATE NOCASE;";
                command.Parameters.AddWithValue("$url", feedUri.AbsoluteUri);
                feedId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                                ?? throw new InvalidOperationException(
                                    _localization.GetString("SubscriptionSaveFailed")));
            }

            var feed = new Feed
            {
                Id = feedId,
                Url = feedUri.AbsoluteUri,
                GroupId = groupId,
                Title = parsedFeed.Title,
                SiteUrl = parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty,
                IconUrl = parsedFeed.IconUri?.AbsoluteUri ?? string.Empty,
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

    public async Task<long?> AddImportedFeedAsync(
        SubscriptionOutline subscription,
        long? groupId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO feeds (
                    url, title, site_url, icon_url, description, group_id, created_utc)
                VALUES (
                    $url, $title, $site_url, '', '', $group_id, $created)
                ON CONFLICT(url) DO NOTHING
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$url", subscription.FeedUri.AbsoluteUri);
            command.Parameters.AddWithValue(
                "$title",
                string.IsNullOrWhiteSpace(subscription.Title)
                    ? subscription.FeedUri.Host
                    : subscription.Title.Trim());
            command.Parameters.AddWithValue(
                "$site_url",
                subscription.SiteUri?.AbsoluteUri ?? string.Empty);
            command.Parameters.AddWithValue(
                "$group_id",
                groupId is null ? DBNull.Value : groupId.Value);
            command.Parameters.AddWithValue("$created", FormatDate(DateTimeOffset.UtcNow));
            return await command.ExecuteScalarAsync(cancellationToken) is long feedId
                ? feedId
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ParsedArticle>> UpdateFeedAsync(
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
                        site_url = $site_url,
                        icon_url = $icon_url,
                        description = $description,
                        last_refreshed_utc = $now,
                        etag = $etag,
                        last_modified_utc = $last_modified
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", feed.Id);
                command.Parameters.AddWithValue("$site_url", parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty);
                command.Parameters.AddWithValue("$icon_url", parsedFeed.IconUri?.AbsoluteUri ?? string.Empty);
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

    public async Task<bool> UpdateFeedSubscriptionAsync(
        long feedId,
        string title,
        string url,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE feeds SET
                    url = $url,
                    title = $title,
                    site_url = CASE WHEN url COLLATE BINARY = $url THEN site_url ELSE '' END,
                    icon_url = CASE WHEN url COLLATE BINARY = $url THEN icon_url ELSE '' END,
                    description = CASE WHEN url COLLATE BINARY = $url THEN description ELSE '' END,
                    last_refreshed_utc = CASE
                        WHEN url COLLATE BINARY = $url THEN last_refreshed_utc
                        ELSE NULL
                    END,
                    etag = CASE WHEN url COLLATE BINARY = $url THEN etag ELSE NULL END,
                    last_modified_utc = CASE
                        WHEN url COLLATE BINARY = $url THEN last_modified_utc
                        ELSE NULL
                    END
                WHERE id = $id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM feeds AS other
                      WHERE other.id <> $id
                        AND other.url = $url COLLATE NOCASE
                  )
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$id", feedId);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$url", url);
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TouchFeedAsync(
        long feedId,
        string iconUrl,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "UPDATE feeds SET icon_url = $icon_url, last_refreshed_utc = $now WHERE id = $id;",
            command =>
            {
                command.Parameters.AddWithValue("$id", feedId);
                command.Parameters.AddWithValue("$icon_url", iconUrl);
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

    public async Task MarkArticlesReadAsync(
        IReadOnlyList<long> articleIds,
        CancellationToken cancellationToken = default)
    {
        if (articleIds.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            foreach (var batch in articleIds.Chunk(500))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var parameterNames = new string[batch.Length];
                for (var index = 0; index < batch.Length; index++)
                {
                    var parameterName = $"$id{index}";
                    parameterNames[index] = parameterName;
                    command.Parameters.AddWithValue(parameterName, batch[index]);
                }

                command.CommandText = $"UPDATE articles SET is_read = 1 WHERE id IN ({string.Join(", ", parameterNames)});";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteFeedAsync(long feedId, CancellationToken cancellationToken = default) =>
        DeleteFeedsAsync([feedId], cancellationToken);

    public Task DeleteFeedsAsync(
        IReadOnlyCollection<long> feedIds,
        CancellationToken cancellationToken = default) =>
        ExecuteForFeedIdsAsync(
            feedIds,
            parameterNames => $"DELETE FROM feeds WHERE id IN ({parameterNames});",
            bind: null,
            cancellationToken);

    public async Task<FeedGroup> AddFeedGroupAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO feed_groups (name, created_utc)
                VALUES ($name, $created)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$created", FormatDate(DateTimeOffset.UtcNow));
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken)
                            ?? throw new InvalidOperationException(
                                _localization.GetString("GroupOperationFailed")));
            return new FeedGroup { Id = id, Name = name };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RenameFeedGroupAsync(
        long groupId,
        string name,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE feed_groups SET name = $name WHERE id = $id;",
            command =>
            {
                command.Parameters.AddWithValue("$id", groupId);
                command.Parameters.AddWithValue("$name", name);
            },
            cancellationToken);

    public async Task DeleteFeedGroupAsync(
        long groupId,
        bool deleteFeeds,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (deleteFeeds)
            {
                await using var feedCommand = connection.CreateCommand();
                feedCommand.Transaction = (SqliteTransaction)transaction;
                feedCommand.CommandText = "DELETE FROM feeds WHERE group_id = $id;";
                feedCommand.Parameters.AddWithValue("$id", groupId);
                await feedCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var groupCommand = connection.CreateCommand())
            {
                groupCommand.Transaction = (SqliteTransaction)transaction;
                groupCommand.CommandText = "DELETE FROM feed_groups WHERE id = $id;";
                groupCommand.Parameters.AddWithValue("$id", groupId);
                await groupCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetFeedGroupAsync(
        long feedId,
        long? groupId,
        CancellationToken cancellationToken = default) =>
        SetFeedsGroupAsync([feedId], groupId, cancellationToken);

    public Task SetFeedsGroupAsync(
        IReadOnlyCollection<long> feedIds,
        long? groupId,
        CancellationToken cancellationToken = default) =>
        ExecuteForFeedIdsAsync(
            feedIds,
            parameterNames => $"UPDATE feeds SET group_id = $group_id WHERE id IN ({parameterNames});",
            command =>
            {
                command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
            },
            cancellationToken);

    private async Task ExecuteForFeedIdsAsync(
        IReadOnlyCollection<long> feedIds,
        Func<string, string> createSql,
        Action<SqliteCommand>? bind,
        CancellationToken cancellationToken)
    {
        var normalizedFeedIds = feedIds.Distinct().ToArray();
        if (normalizedFeedIds.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var parameterNames = new string[normalizedFeedIds.Length];
            for (var index = 0; index < normalizedFeedIds.Length; index++)
            {
                var parameterName = $"$id_{index}";
                parameterNames[index] = parameterName;
                command.Parameters.AddWithValue(parameterName, normalizedFeedIds[index]);
            }

            bind?.Invoke(command);
            command.CommandText = createSql(string.Join(", ", parameterNames));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

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

    private async Task<List<ParsedArticle>> UpsertArticlesCoreAsync(
        SqliteConnection connection,
        long feedId,
        IReadOnlyList<ParsedArticle> articles,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var insertedArticles = new List<ParsedArticle>();
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
                insertedArticles.Add(article);
                continue;
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE articles SET
                    title = $title,
                    link = $link,
                    author = $author,
                    published_utc = $published,
                    summary = $summary,
                    content = $content
                WHERE feed_id = $feed_id AND external_id = $external_id;
                """;
            updateCommand.Parameters.AddWithValue("$feed_id", feedId);
            updateCommand.Parameters.AddWithValue("$external_id", article.ExternalId);
            updateCommand.Parameters.AddWithValue("$title", article.Title);
            updateCommand.Parameters.AddWithValue("$link", article.Link?.AbsoluteUri ?? string.Empty);
            updateCommand.Parameters.AddWithValue("$author", article.Author);
            updateCommand.Parameters.AddWithValue("$published", FormatNullableDate(article.PublishedAt));
            updateCommand.Parameters.AddWithValue("$summary", article.Summary);
            updateCommand.Parameters.AddWithValue("$content", article.Content);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return insertedArticles;
    }

    private static async Task MigrateArticleContentToHtmlAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (version >= 1)
        {
            return;
        }

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = """
            UPDATE feeds SET
                etag = NULL,
                last_modified_utc = NULL
            WHERE EXISTS (
                SELECT 1 FROM articles WHERE articles.feed_id = feeds.id
            );
            PRAGMA user_version = 1;
            """;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateFunction<string?, string?, string?, string?, int>(
            "article_search_rank",
            ArticleSearchMatcher.GetMatchRank,
            isDeterministic: true);
        return connection;
    }

    private static void AddFeedParameters(
        SqliteCommand command,
        Uri feedUri,
        ParsedFeed parsedFeed,
        string? etag,
        DateTimeOffset? lastModifiedAt,
        long? groupId)
    {
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$url", feedUri.AbsoluteUri);
        command.Parameters.AddWithValue("$title", parsedFeed.Title);
        command.Parameters.AddWithValue("$site_url", parsedFeed.SiteUri?.AbsoluteUri ?? string.Empty);
        command.Parameters.AddWithValue("$icon_url", parsedFeed.IconUri?.AbsoluteUri ?? string.Empty);
        command.Parameters.AddWithValue("$description", parsedFeed.Description);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_modified", FormatNullableDate(lastModifiedAt));
        command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
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
