using System.Security.Principal;
using Microsoft.Win32;

namespace Porta.Services;

/// <summary>
/// The signed-in user's Windows account picture and name: the same ones on the lock screen and
/// in Settings › Accounts, which for a Microsoft account is the picture set on that account.
///
/// Windows keeps copies of the picture for each user in C:\Users\Public\AccountPictures\&lt;SID&gt;
/// and lists them under HKLM\...\AccountPicture\Users\&lt;SID&gt;. Reading that needs no special
/// permission, so it works in the Store and GitHub builds alike. The WinRT User API is the fallback.
/// </summary>
public static class AccountPicture
{
    // Largest first; Porta shows 64 px at up to 300% scaling, so 208 or bigger looks sharp.
    private static readonly string[] Sizes = ["Image448", "Image424", "Image240", "Image208", "Image192", "Image96"];

    /// <summary>Path to the best account picture on disk, or null if the user has none.</summary>
    public static string? FindPicturePath()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            if (key is null) return null;
            foreach (var size in Sizes)
            {
                if (key.GetValue(size) is string path && File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
        return null;
    }

    /// <summary>
    /// Copies the picture via the WinRT User API into Porta's folder. Used when the registry route
    /// finds nothing (rare, e.g. a picture that hasn't synced to this PC's cache yet).
    /// </summary>
    public static async Task<string?> FetchWithUserApiAsync()
    {
        try
        {
            var users = await Windows.System.User.FindAllAsync(
                Windows.System.UserType.LocalUser, Windows.System.UserAuthenticationStatus.LocallyAuthenticated);
            var user = users.FirstOrDefault();
            if (user is null) return null;

            var reference = await user.GetPictureAsync(Windows.System.UserPictureSize.Size424x424);
            if (reference is null) return null;

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porta");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "account-picture.png");

            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            using var output = File.Create(target);
            await input.CopyToAsync(output);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The name to show: the Microsoft account's display name when Windows will share it
    /// (Store build with the userAccountInformation capability), else the Windows display name,
    /// else the sign-in name.
    /// </summary>
    public static async Task<string> GetDisplayNameAsync()
    {
        try
        {
            var users = await Windows.System.User.FindAllAsync(
                Windows.System.UserType.LocalUser, Windows.System.UserAuthenticationStatus.LocallyAuthenticated);
            if (users.FirstOrDefault() is { } user &&
                await user.GetPropertyAsync(Windows.System.KnownUserProperties.DisplayName) is string name &&
                !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (Exception)
        {
        }
        return Interop.Native.GetDisplayName();
    }
}
