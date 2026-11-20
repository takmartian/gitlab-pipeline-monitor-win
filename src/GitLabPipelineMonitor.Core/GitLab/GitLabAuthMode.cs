using GitLabPipelineMonitor.Core.Localization;

namespace GitLabPipelineMonitor.Core.GitLab;

/// <summary>令牌类型：GitLab 认两种头。</summary>
public enum GitLabAuthMode
{
    /// <summary>PRIVATE-TOKEN 头（个人访问令牌）。</summary>
    Pat,

    /// <summary>Authorization: Bearer（OAuth 令牌）。</summary>
    Oauth
}

public static class GitLabAuthModeExtensions
{
    public static string Label(this GitLabAuthMode mode) => mode switch
    {
        GitLabAuthMode.Pat => Loc.T("个人访问令牌 (PRIVATE-TOKEN)"),
        _ => Loc.T("OAuth 令牌 (Bearer)")
    };

    /// <summary>存储用的字符串。</summary>
    public static string ToStorageString(this GitLabAuthMode mode) => mode == GitLabAuthMode.Oauth ? "oauth" : "pat";

    public static GitLabAuthMode Parse(string? raw) => raw == "oauth" ? GitLabAuthMode.Oauth : GitLabAuthMode.Pat;
}
