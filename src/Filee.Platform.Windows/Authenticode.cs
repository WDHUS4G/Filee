// Authenticode signatures of programs: whether Windows trusts a file's signature, and who signed it.
//
// The in-app updater checks the installer it downloaded with this before running it. The SHA-256 from the release's
// checksum file only shows the download arrived intact; whoever could replace the installer on the release could
// replace that file too. The signature shows the installer comes from the publisher that signed this copy of Filee.
// WinVerifyTrust checks the signature and its chain, and the signer is read from the same check, so the certificate
// compared is the one Windows verified.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Filee.Platform.Windows;

/// <summary>Authenticode checks. Every member is safe to call on any OS.</summary>
public static partial class Authenticode
{
    /// <summary>
    /// The subject of the certificate that signed <paramref name="path"/> (e.g. "CN=Open Source Developer, …"), or null
    /// when the file is not signed, Windows does not trust its signature, or this is not Windows.
    /// </summary>
    /// <param name="checkRevocation">
    /// Also asks the certificate authorities whether a certificate in the chain was revoked (needs the network).
    /// </param>
    public static string? TrustedSigner(string path, bool checkRevocation)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Verify(path, checkRevocation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or CryptographicException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? Verify(string path, bool checkRevocation)
    {
        var action = GenericVerifyV2;
        fixed (char* file = Path.GetFullPath(path))
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = (nint)file };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = checkRevocation ? WTD_REVOKE_WHOLECHAIN : WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (nint)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = checkRevocation ? WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT : 0,
            };
            try
            {
                if (WinVerifyTrust(InvalidWindow, ref action, ref data) != 0)
                    return null;
                // The signer's own certificate: the first one of the first signer's chain.
                var provider = WTHelperProvDataFromStateData(data.hWVTStateData);
                var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                var certificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
                if (certificate == 0)
                    return null;
                // CRYPT_PROVIDER_CERT: a DWORD cbStruct, then (aligned to a pointer) the PCCERT_CONTEXT.
                var context = Marshal.ReadIntPtr(certificate, IntPtr.Size);
                if (context == 0)
                    return null;
                using var x509 = new X509Certificate2(context);
                return x509.Subject;
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(InvalidWindow, ref action, ref data);
            }
        }
    }

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2: the Authenticode policy.</summary>
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>INVALID_HANDLE_VALUE: no window, so WinVerifyTrust never asks the user.</summary>
    private const nint InvalidWindow = -1;

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid action, ref WINTRUST_DATA data);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
}
