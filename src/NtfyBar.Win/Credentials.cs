using Meziantou.Framework.Win32;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Windows Credential Manager (DPAPI, current user). Targets <c>ntfy-bar/password</c> and
/// <c>ntfy-bar/token</c>, visible under Control Panel › Credential Manager › Windows Credentials.</summary>
internal static class Credentials
{
    public const string Password = "ntfy-bar/password";
    public const string Token = "ntfy-bar/token";

    public static string Get(string target)
    {
        try { return CredentialManager.ReadCredential(target)?.Password ?? ""; }
        catch (Exception e) { Log.Write($"credentials: read {target} failed: {e.Message}"); return ""; }
    }

    /// <summary>Empty value deletes the credential.</summary>
    public static void Set(string target, string userName, string value)
    {
        if (string.IsNullOrEmpty(value)) { Delete(target); return; }
        try
        {
            CredentialManager.WriteCredential(target, string.IsNullOrEmpty(userName) ? "ntfy-bar" : userName, value,
                CredentialPersistence.LocalMachine);
        }
        catch (Exception e) { Log.Write($"credentials: write {target} failed: {e.Message}"); }
    }

    public static void Delete(string target)
    {
        try
        {
            if (CredentialManager.ReadCredential(target) is not null) CredentialManager.DeleteCredential(target);
        }
        catch (Exception e) { Log.Write($"credentials: delete {target} failed: {e.Message}"); }
    }
}
