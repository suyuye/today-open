using System.Globalization;
using Microsoft.Data.Sqlite;
using NoteApp.Helpers;

namespace NoteApp.Data.Repositories;

/// <summary>设置键值对数据访问实现（UPSERT 读写，异常记录后向上抛出）。</summary>
public class SettingsRepository : ISettingsRepository
{
    private readonly AppDbContext _dbContext;

    public SettingsRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public string? GetValue(string key) => Execute("GetValue", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    });

    public T GetValue<T>(string key, T defaultValue)
    {
        var value = GetValue(key);
        return value is null ? defaultValue : ConvertValue(value, defaultValue);
    }

    public void SetValue(string key, string value) => Execute("SetValue", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO Settings (Key, Value, UpdatedAt) VALUES ($key, $value, $updatedAt);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", DateTimeHelper.ToStorage(DateTime.Now));
        command.ExecuteNonQuery();
    });

    public void SetValue<T>(string key, T value) => SetValue(key, ToStorageString(value));

    public Dictionary<string, string> GetAll() => Execute("GetAll", () =>
    {
        var settings = new Dictionary<string, string>();
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings ORDER BY Key ASC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }
        return settings;
    });

    public bool Exists(string key) => Execute("Exists", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    });

    public void Remove(string key) => Execute("Remove", () =>
    {
        using var connection = _dbContext.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    });

    private static T ConvertValue<T>(string value, T defaultValue)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType.IsEnum)
        {
            return Enum.TryParse(targetType, value, ignoreCase: true, out var result)
                ? (T)result
                : defaultValue;
        }

        try
        {
            return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LogHelper.Error($"设置值转换失败：类型 {targetType.Name}，值 \"{value}\"", ex);
            return defaultValue;
        }
    }

    private static string ToStorageString<T>(T value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (type.IsEnum)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture)!.ToLowerInvariant();
        }

        if (type == typeof(bool))
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"SettingsRepository.{operation} 执行失败", ex);
            throw;
        }
    }

    private static void Execute(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogHelper.Error($"SettingsRepository.{operation} 执行失败", ex);
            throw;
        }
    }
}
