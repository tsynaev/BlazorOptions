using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using BlazorOptions.Server.Models;
using BlazorOptions.Server.Options;

namespace BlazorOptions.Server.Services;

public class UserRegistryService
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public UserRegistryService(IWebHostEnvironment environment, IOptions<DataStorageOptions> dataOptions)
    {
        var dataRoot = ResolveDataRoot(environment.ContentRootPath, dataOptions.Value.Path);
        Directory.CreateDirectory(dataRoot);
        _dbPath = Path.Combine(dataRoot, "users.db");
        EnsureDatabase();
    }

    public async Task<(bool Success, string? Error, AuthResponse? Response, string? UserId)> RegisterAsync(
        string userName,
        string password,
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "User name and password are required.", null, null);
        }

        var normalized = userName.Trim();
        await _mutex.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = "SELECT COUNT(1) FROM Users WHERE UserName = $userName";
            existsCommand.Parameters.AddWithValue("$userName", normalized);
            var exists = (long)(await existsCommand.ExecuteScalarAsync() ?? 0L);
            if (exists > 0)
            {
                return (false, "User name is already registered.", null, null);
            }

            var userId = Guid.NewGuid().ToString("N");
            var (hash, salt) = HashPassword(password);
            var token = Guid.NewGuid().ToString("N");

            var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO Users (Id, UserName, PasswordHash, PasswordSalt, Token, CreatedUtc)
                VALUES ($id, $userName, $hash, $salt, $token, $createdUtc)
                """;
            insertCommand.Parameters.AddWithValue("$id", userId);
            insertCommand.Parameters.AddWithValue("$userName", normalized);
            insertCommand.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
            insertCommand.Parameters.Add("$salt", SqliteType.Blob).Value = salt;
            insertCommand.Parameters.AddWithValue("$token", token);
            insertCommand.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync();

            var tokenInsertCommand = connection.CreateCommand();
            tokenInsertCommand.CommandText = """
                INSERT OR IGNORE INTO UserTokens (Token, UserId, DeviceId, CreatedUtc)
                VALUES ($token, $userId, $deviceId, $createdUtc)
                """;
            tokenInsertCommand.Parameters.AddWithValue("$token", token);
            tokenInsertCommand.Parameters.AddWithValue("$userId", userId);
            tokenInsertCommand.Parameters.AddWithValue("$deviceId", deviceId);
            tokenInsertCommand.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
            await tokenInsertCommand.ExecuteNonQueryAsync();

            return (true, null, new AuthResponse(normalized, token, deviceId), userId);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<(bool Success, string? Error, AuthResponse? Response)> LoginAsync(
        string userName,
        string password,
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "User name and password are required.", null);
        }

        var normalized = userName.Trim();
        await _mutex.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = """
                SELECT Id, PasswordHash, PasswordSalt
                FROM Users
                WHERE UserName = $userName
                """;
            selectCommand.Parameters.AddWithValue("$userName", normalized);
            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return (false, "Invalid credentials.", null);
            }

            var userId = reader.GetString(0);
            var hash = (byte[])reader["PasswordHash"];
            var salt = (byte[])reader["PasswordSalt"];

            if (!VerifyPassword(password, hash, salt))
            {
                return (false, "Invalid credentials.", null);
            }

            var token = Guid.NewGuid().ToString("N");
            var resolvedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId.Trim();
            var tokenInsertCommand = connection.CreateCommand();
            tokenInsertCommand.CommandText = """
                INSERT OR IGNORE INTO UserTokens (Token, UserId, DeviceId, CreatedUtc)
                VALUES ($token, $userId, $deviceId, $createdUtc)
                """;
            tokenInsertCommand.Parameters.AddWithValue("$token", token);
            tokenInsertCommand.Parameters.AddWithValue("$userId", userId);
            tokenInsertCommand.Parameters.AddWithValue("$deviceId", resolvedDeviceId);
            tokenInsertCommand.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
            await tokenInsertCommand.ExecuteNonQueryAsync();

            return (true, null, new AuthResponse(normalized, token, resolvedDeviceId));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<UserRecord?> GetUserByTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await _mutex.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = """
                SELECT Users.Id, Users.UserName
                FROM UserTokens
                JOIN Users ON Users.Id = UserTokens.UserId
                WHERE UserTokens.Token = $token
                """;
            selectCommand.Parameters.AddWithValue("$token", token.Trim());
            await using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserRecord(reader.GetString(0), reader.GetString(1), token.Trim());
            }
            return null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> LogoutAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        await _mutex.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM UserTokens WHERE Token = $token";
            deleteCommand.Parameters.AddWithValue("$token", token.Trim());
            var removed = await deleteCommand.ExecuteNonQueryAsync();
            return removed > 0;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void EnsureDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (
                Id TEXT PRIMARY KEY,
                UserName TEXT NOT NULL UNIQUE,
                PasswordHash BLOB NOT NULL,
                PasswordSalt BLOB NOT NULL,
                Token TEXT NULL,
                CreatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS UserTokens (
                Token TEXT PRIMARY KEY,
                UserId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(32);
        return (hash, salt);
    }

    private static bool VerifyPassword(string password, byte[] hash, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        var computed = pbkdf2.GetBytes(32);
        return CryptographicOperations.FixedTimeEquals(computed, hash);
    }

    private static string ResolveDataRoot(string contentRootPath, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(contentRootPath, "Data");
        }

        var trimmed = configuredPath.Trim();
        return Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.GetFullPath(Path.Combine(contentRootPath, trimmed));
    }
}
