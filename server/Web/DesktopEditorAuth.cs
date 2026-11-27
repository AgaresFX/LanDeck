using System.Security.Cryptography;
using System.Text;

namespace PadNook.Web;

/// <summary>
/// Key of the desktop editor window: random per process and injected only into the WebView2 page that this
/// process hosts, so no other device (or browser tab) can use it.
/// </summary>
public static class DesktopEditorAuth
{
    public const string DeviceId = "desktop-editor";

    public static readonly string Key =
#if DEBUG
        // Debug builds only: a fixed key lets the desktop mode be tested in a normal browser.
        Environment.GetEnvironmentVariable("PADNOOK_DEV") == "1" && Environment.GetEnvironmentVariable("PADNOOK_DEV_DESKTOPKEY") is { Length: >= 8 } k ? k :
#endif
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public static bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Key));
}
