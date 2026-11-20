using GitLabPipelineMonitor.Core.Storage;

namespace GitLabPipelineMonitor.Tests;

/// <summary>令牌加密存储：按主机名分账户、密钥来自机器指纹、换机器解不开。</summary>
public static class TokenStoreTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "glpm-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(path);
        return path;
    }

    public static Task Run()
    {
        var dir = TempDir();
        var host = new FakeHost();
        var store = new TokenStore(host, dir);

        Check.IsNull(store.Password("gitlab.example.com"), "新账户没有令牌");
        store.Set("glpat-secret", "gitlab.example.com");
        Check.Equal("glpat-secret", store.Password("gitlab.example.com") ?? "", "存进去能取出来");

        // 按主机名分账户，互不覆盖
        store.Set("other-token", "gitlab.internal");
        Check.Equal("glpat-secret", store.Password("gitlab.example.com") ?? "", "A 账户不受 B 影响");
        Check.Equal("other-token", store.Password("gitlab.internal") ?? "", "B 账户独立");
        Check.Equal(2, store.Accounts().Count, "两个账户都在");

        store.Set("glpat-v2", "gitlab.example.com");
        Check.Equal("glpat-v2", store.Password("gitlab.example.com") ?? "", "同账户覆盖");

        store.Set("", "gitlab.example.com");
        Check.IsNull(store.Password("gitlab.example.com"), "存空串等于删除");

        store.Remove("gitlab.internal");
        Check.IsNull(store.Password("gitlab.internal"), "删除后取不到");

        // 密文不能是明文（真的加密了）
        store.Set("glpat-plain-lookalike", "a.example");
        var raw = File.ReadAllText(store.FilePath);
        Check.NotContains("glpat-plain-lookalike", raw, "文件里不该出现明文令牌");

        // 换机器（指纹变了）解不开 —— 这就是「密钥不落盘」的含义
        var otherHost = new FakeHost { MachineFingerprint = "another-machine" };
        Check.Equal("解密失败", new TokenStore(otherHost, dir).Decryptable("a.example"), "换机器后解密失败");

        // 换用户名也解不开
        var otherUser = new FakeHost { UserName = "someone-else" };
        Check.Equal("解密失败", new TokenStore(otherUser, dir).Decryptable("a.example"), "换用户后解密失败");

        Check.Equal("无记录", store.Decryptable("never.used"), "没存过的账户报无记录");

        // 文件被手改坏不该让程序起不来
        File.WriteAllText(store.FilePath, "{ 这不是 JSON");
        Check.IsNull(store.Password("a.example"), "文件坏了当作没有令牌");

        Directory.Delete(dir, true);
        Program.Ok("令牌加密存储");
        return Task.CompletedTask;
    }
}
