using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitLabPipelineMonitor.Core.Platform;

namespace GitLabPipelineMonitor.Core.Storage;

/// <summary>
/// 令牌存本地加密文件，不用系统凭据库。
///
/// 加密：AES-GCM（.NET 内置，零第三方依赖）。
/// 密钥 = SHA256(固定前缀 + 机器指纹 + 当前用户名 + 每条记录独立随机 salt)，密钥本身不落盘，
/// 所以密文拷到别的机器或别的账户下都解不开。按服务器主机名分账户，切换实例互不覆盖。
///
/// 为什么不用 Windows 凭据管理器 / DPAPI：DPAPI 在 .NET 里是独立 NuGet 包（会破掉零依赖卖点），
/// 而且这条设计与 macOS 版一致 —— 两端都是「加密文件 + 机器指纹派生密钥」，行为可预期、可测试。
/// </summary>
public sealed class TokenStore
{
    private const string Prefix = "glpm.token.v1|";
    private const int SaltSize = 16;

    private sealed class Record
    {
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [JsonPropertyName("blob")] public string Blob { get; set; } = "";
    }

    private readonly IHostServices _host;
    private readonly string _directory;

    public TokenStore(IHostServices host, string? directory = null)
    {
        _host = host;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
    }

    public string TokenDirectory => _directory;
    public string FilePath => Path.Combine(_directory, "tokens.json");

    // MARK: - 读写

    public string? Password(string account)
    {
        var records = ReadRecords();
        if (!records.TryGetValue(account, out var record)) return null;
        return Decrypt(record, account);
    }

    public void Set(string value, string account)
    {
        if (string.IsNullOrEmpty(value)) { Remove(account); return; }
        var records = ReadRecords();
        records[account] = Encrypt(value);
        WriteRecords(records);
    }

    public void Remove(string account)
    {
        var records = ReadRecords();
        if (records.Remove(account)) WriteRecords(records);
    }

    /// <summary>已存放令牌的账户（诊断用）。</summary>
    public IReadOnlyList<string> Accounts() => ReadRecords().Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>某账户的令牌能否正常解密（诊断用，不暴露内容）。</summary>
    public string Decryptable(string account)
    {
        var records = ReadRecords();
        if (!records.TryGetValue(account, out var record)) return "无记录";
        return Decrypt(record, account) is not null ? "可解密" : "解密失败";
    }

    // MARK: - 文件

    private Dictionary<string, Record> ReadRecords()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, Record>();
            var map = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(FilePath), TokenJsonOptions.ReadRecord);
            return map ?? new Dictionary<string, Record>();
        }
        catch (Exception)
        {
            // 文件被手改坏了：当成没有令牌，而不是让程序起不来
            return new Dictionary<string, Record>();
        }
    }

    private void WriteRecords(Dictionary<string, Record> records)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(records, TokenJsonOptions.WriteRecord);
        // 先写临时文件再替换：避免写一半掉电把整个令牌文件毁掉
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
        else File.Move(temp, FilePath);
    }

    // 文件权限：令牌文件在 %APPDATA% 下，该目录的默认 ACL 已经是「仅当前用户可访问」。
    // 想再收紧一层要引 System.IO.FileSystem.AccessControl 包，为零依赖不做。
    // MARK: - 加解密

    private Record Encrypt(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(12);   // GCM 标准 96 位 nonce
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(salt, _host.MachineFingerprint, _host.UserName), 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        // 存成 base64 的 nonce|tag|cipher，解密时按长度切回来
        var combined = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, combined, nonce.Length + tag.Length, cipher.Length);
        return new Record { Salt = Convert.ToHexString(salt).ToLowerInvariant(), Blob = Convert.ToBase64String(combined) };
    }

    private string? Decrypt(Record record, string account)
    {
        byte[] salt, combined;
        try
        {
            salt = Convert.FromHexString(record.Salt);
            combined = Convert.FromBase64String(record.Blob);
        }
        catch (FormatException)
        {
            return null;
        }
        if (combined.Length < 28) return null;   // 至少要装下 nonce + tag

        var nonce = combined[..12];
        var tag = combined[12..28];
        var cipher = combined[28..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(salt, _host.MachineFingerprint, _host.UserName), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            // 换过机器或账户，密钥对不上
            return null;
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key(byte[] salt, string fingerprint, string userName)
    {
        return SHA256.HashData(
            Concat(Encoding.UTF8.GetBytes(Prefix), Encoding.UTF8.GetBytes(fingerprint),
                Encoding.UTF8.GetBytes(userName), salt));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}

internal static class TokenJsonOptions
{
    public static readonly JsonSerializerOptions ReadRecord = new() { PropertyNameCaseInsensitive = true };
    public static readonly JsonSerializerOptions WriteRecord = new() { WriteIndented = false };
}
