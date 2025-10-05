using HyggePlay.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace HyggePlay.Services
{
    public sealed class DatabaseService : IAsyncDisposable
    {
    private const int SchemaVersion = 1;

    private readonly string _databasePath;
    private readonly string _connectionString;

        public DatabaseService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "HyggePlay");
            Directory.CreateDirectory(appFolder);
            _databasePath = Path.Combine(appFolder, "hyggeplay.db");
            _connectionString = $"Data Source={_databasePath};Foreign Keys=True";
        }

        public async Task InitializeAsync()
        {
            await InitializeDatabaseAsync();
        }

        private async Task InitializeDatabaseAsync()
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();
            int currentVersion = await GetSchemaVersionAsync(connection);

            if (currentVersion < SchemaVersion)
            {
                await RebuildSchemaAsync(connection);
            }

            await EnsureSchemaAsync(connection);
            await SetSchemaVersionAsync(connection, SchemaVersion);
        }

        private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            object? result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        private static async Task SetSchemaVersionAsync(SqliteConnection connection, int version)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version};";
            await command.ExecuteNonQueryAsync();
        }

        private static async Task RebuildSchemaAsync(SqliteConnection connection)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA foreign_keys = OFF;
                DROP TABLE IF EXISTS Channels;
                DROP TABLE IF EXISTS ChannelGroups;
                PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync();
        }

        private static async Task EnsureSchemaAsync(SqliteConnection connection)
        {
            string schema = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DisplayName TEXT NOT NULL,
                ServerUrl TEXT NOT NULL,
                Username TEXT NOT NULL,
                Password TEXT NOT NULL,
                UNIQUE(ServerUrl, Username)
            );

            CREATE TABLE IF NOT EXISTS ChannelGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                GroupIdentifier TEXT NOT NULL,
                Name TEXT NOT NULL,
                UNIQUE(UserId, GroupIdentifier),
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Channels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                GroupIdentifier TEXT NOT NULL,
                ChannelIdentifier INTEGER NOT NULL,
                Name TEXT NOT NULL,
                StreamIcon TEXT,
                StreamUrl TEXT,
                UNIQUE(UserId, ChannelIdentifier),
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                FOREIGN KEY(UserId, GroupIdentifier) REFERENCES ChannelGroups(UserId, GroupIdentifier) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_channels_user_group_name
                ON Channels(UserId, GroupIdentifier, Name COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_channels_user_name
                ON Channels(UserId, Name COLLATE NOCASE);
        ";

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = schema;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> UpsertUserAsync(UserProfile profile)
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Users (DisplayName, ServerUrl, Username, Password)
                VALUES ($displayName, $serverUrl, $username, $password)
                ON CONFLICT(ServerUrl, Username) DO UPDATE SET
                    DisplayName = excluded.DisplayName,
                    Password = excluded.Password
                RETURNING Id";
            
            command.Parameters.AddWithValue("$displayName", profile.DisplayName);
            command.Parameters.AddWithValue("$serverUrl", profile.ServerUrl);
            command.Parameters.AddWithValue("$username", profile.Username);
            command.Parameters.AddWithValue("$password", profile.Password);

            object? result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<List<UserProfile>> GetUsersAsync()
        {
            List<UserProfile> users = new();
            
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT Id, DisplayName, ServerUrl, Username, Password FROM Users ORDER BY DisplayName";
            
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                users.Add(new UserProfile
                {
                    Id = reader.GetInt32(0),
                    DisplayName = reader.GetString(1),
                    ServerUrl = reader.GetString(2),
                    Username = reader.GetString(3),
                    Password = reader.GetString(4)
                });
            }
            
            return users;
        }

        public async Task DeleteUserAsync(int userId)
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM Channels WHERE UserId = $userId;
                DELETE FROM ChannelGroups WHERE UserId = $userId;
                DELETE FROM Users WHERE Id = $userId;";
            command.Parameters.AddWithValue("$userId", userId);
            
            await command.ExecuteNonQueryAsync();
        }

        public async Task ReplaceChannelDataAsync(int userId, IReadOnlyList<ChannelGroupInfo> groups, IReadOnlyList<ChannelInfo> channels)
        {
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            int fkState = 0;
            await using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys;";
                object? result = await pragma.ExecuteScalarAsync();
                fkState = Convert.ToInt32(result);
            }

            await LogService.LogInfoAsync("ReplaceChannelDataAsync begin", new Dictionary<string, string>
            {
                { "userId", userId.ToString() },
                { "groupCount", groups.Count.ToString() },
                { "channelCount", channels.Count.ToString() },
                { "fkEnabled", fkState.ToString() }
            });

            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            try
            {
                // Clear existing data
                await using SqliteCommand deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = @"
                    DELETE FROM Channels WHERE UserId = $userId;
                    DELETE FROM ChannelGroups WHERE UserId = $userId;";
                deleteCommand.Parameters.AddWithValue("$userId", userId);
                await deleteCommand.ExecuteNonQueryAsync();

                Dictionary<string, string> groupNameLookup = new(StringComparer.OrdinalIgnoreCase);
                foreach (ChannelGroupInfo group in groups)
                {
                    if (!string.IsNullOrWhiteSpace(group.GroupId))
                    {
                        groupNameLookup[group.GroupId] = string.IsNullOrWhiteSpace(group.Name) ? group.GroupId : group.Name;
                    }
                }

                HashSet<string> insertedGroupIds = new(StringComparer.OrdinalIgnoreCase);

                // Insert groups
                if (groups.Count > 0)
                {
                    await using SqliteCommand groupCommand = connection.CreateCommand();
                    groupCommand.Transaction = transaction;
                    groupCommand.CommandText = @"INSERT OR REPLACE INTO ChannelGroups (UserId, GroupIdentifier, Name) VALUES ($userId, $groupId, $name)";
                    
                    foreach (ChannelGroupInfo group in groups)
                    {
                        if (string.IsNullOrWhiteSpace(group.GroupId))
                        {
                            continue;
                        }

                        groupCommand.Parameters.Clear();
                        groupCommand.Parameters.AddWithValue("$userId", userId);
                        groupCommand.Parameters.AddWithValue("$groupId", group.GroupId);
                        groupCommand.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(group.Name) ? group.GroupId : group.Name);
                        await groupCommand.ExecuteNonQueryAsync();
                        insertedGroupIds.Add(group.GroupId);
                    }
                }

                // Always create a default group for channels without groups
                const string defaultGroupId = "default";
                groupNameLookup[defaultGroupId] = "All Channels";

                await using SqliteCommand defaultGroupCommand = connection.CreateCommand();
                defaultGroupCommand.Transaction = transaction;
                defaultGroupCommand.CommandText = @"INSERT OR IGNORE INTO ChannelGroups (UserId, GroupIdentifier, Name) VALUES ($userId, $groupId, $name)";
                defaultGroupCommand.Parameters.AddWithValue("$userId", userId);
                defaultGroupCommand.Parameters.AddWithValue("$groupId", defaultGroupId);
                defaultGroupCommand.Parameters.AddWithValue("$name", groupNameLookup[defaultGroupId]);
                await defaultGroupCommand.ExecuteNonQueryAsync();
                insertedGroupIds.Add(defaultGroupId);

                await using SqliteCommand ensureGroupExistsCommand = connection.CreateCommand();
                ensureGroupExistsCommand.Transaction = transaction;
                ensureGroupExistsCommand.CommandText = @"INSERT OR IGNORE INTO ChannelGroups (UserId, GroupIdentifier, Name) VALUES ($userId, $groupId, $name)";

                // Insert channels
                if (channels.Count > 0)
                {
                    await using SqliteCommand channelCommand = connection.CreateCommand();
                    channelCommand.Transaction = transaction;
                    channelCommand.CommandText = @"INSERT INTO Channels (UserId, GroupIdentifier, ChannelIdentifier, Name, StreamIcon, StreamUrl)
                                                   VALUES ($userId, $groupId, $channelId, $name, $icon, $url)";
                    
                    foreach (ChannelInfo channel in channels)
                    {
                        string channelGroupId = string.IsNullOrWhiteSpace(channel.GroupId) ? defaultGroupId : channel.GroupId;

                        if (!insertedGroupIds.Contains(channelGroupId))
                        {
                            string groupName = groupNameLookup.TryGetValue(channelGroupId, out string? lookupName) && !string.IsNullOrWhiteSpace(lookupName)
                                ? lookupName
                                : channelGroupId == "0" ? "All Channels" : $"Group {channelGroupId}";

                            ensureGroupExistsCommand.Parameters.Clear();
                            ensureGroupExistsCommand.Parameters.AddWithValue("$userId", userId);
                            ensureGroupExistsCommand.Parameters.AddWithValue("$groupId", channelGroupId);
                            ensureGroupExistsCommand.Parameters.AddWithValue("$name", groupName);
                            await ensureGroupExistsCommand.ExecuteNonQueryAsync();
                            insertedGroupIds.Add(channelGroupId);
                            groupNameLookup[channelGroupId] = groupName;
                        }

                        channelCommand.Parameters.Clear();
                        channelCommand.Parameters.AddWithValue("$userId", userId);
                        channelCommand.Parameters.AddWithValue("$groupId", channelGroupId);
                        channelCommand.Parameters.AddWithValue("$channelId", channel.ChannelId);
                        channelCommand.Parameters.AddWithValue("$name", channel.Name);
                        channelCommand.Parameters.AddWithValue("$icon", channel.StreamIcon ?? (object)DBNull.Value);
                        channelCommand.Parameters.AddWithValue("$url", channel.StreamUrl ?? (object)DBNull.Value);

                        try
                        {
                            await channelCommand.ExecuteNonQueryAsync();
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                        {
                            Dictionary<string, string> metadata = new()
                            {
                                { "userId", userId.ToString() },
                                { "channelId", channel.ChannelId.ToString() },
                                { "channelName", channel.Name },
                                { "channelGroupId", channelGroupId },
                                { "groupsKnown", string.Join(',', insertedGroupIds) }
                            };

                            await LogService.LogErrorAsync("Foreign key failure inserting channel", ex, metadata);

                            throw new InvalidOperationException($"Foreign key constraint failed while inserting channel '{channel.Name}' ({channel.ChannelId}) in group '{channelGroupId}'. See log at {LogService.GetLogFilePath()} for details.", ex);
                        }
                    }
                }

                await transaction.CommitAsync();
                await LogService.LogInfoAsync("ReplaceChannelDataAsync success", new Dictionary<string, string>
                {
                    { "userId", userId.ToString() },
                    { "groupCount", groups.Count.ToString() },
                    { "channelCount", channels.Count.ToString() }
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                await LogService.LogErrorAsync("ReplaceChannelDataAsync failed", ex, new Dictionary<string, string>
                {
                    { "userId", userId.ToString() },
                    { "groupCount", groups.Count.ToString() },
                    { "channelCount", channels.Count.ToString() }
                });
                throw;
            }
        }

        public async Task<List<ChannelGroupInfo>> GetChannelGroupsAsync(int userId)
        {
            List<ChannelGroupInfo> groups = new();
            
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT GroupIdentifier, Name FROM ChannelGroups WHERE UserId = $userId ORDER BY Name";
            command.Parameters.AddWithValue("$userId", userId);
            
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                groups.Add(new ChannelGroupInfo
                {
                    GroupId = reader.GetString(0),
                    Name = reader.GetString(1)
                });
            }
            
            return groups;
        }

        public async Task<List<ChannelInfo>> SearchChannelsAsync(int userId, string? groupId, string? query, int? limit = null)
        {
            List<ChannelInfo> channels = new();
            
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync();

            string sql = @"SELECT c.ChannelIdentifier, c.Name, c.GroupIdentifier, c.StreamIcon, c.StreamUrl, cg.Name AS GroupName 
                          FROM Channels c 
                          LEFT JOIN ChannelGroups cg ON c.GroupIdentifier = cg.GroupIdentifier AND cg.UserId = $userId 
                          WHERE c.UserId = $userId";
            List<SqliteParameter> parameters = new() { new("$userId", userId) };

            if (!string.IsNullOrEmpty(groupId))
            {
                sql += " AND c.GroupIdentifier = $groupId";
                parameters.Add(new("$groupId", groupId));
            }

            if (!string.IsNullOrEmpty(query))
            {
                sql += " AND c.Name LIKE $query";
                parameters.Add(new("$query", $"%{query}%"));
            }

            sql += " ORDER BY c.Name";
            if (limit.HasValue && limit.Value > 0)
            {
                sql += " LIMIT $limit";
                parameters.Add(new("$limit", limit.Value));
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var param in parameters)
            {
                command.Parameters.Add(param);
            }
            
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                channels.Add(new ChannelInfo
                {
                    ChannelId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    GroupId = reader.GetString(2),
                    StreamIcon = reader.IsDBNull(3) ? null : reader.GetString(3),
                    StreamUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                    GroupName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }
            
            return channels;
        }

        private SqliteConnection CreateConnection() => new(_connectionString);

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
