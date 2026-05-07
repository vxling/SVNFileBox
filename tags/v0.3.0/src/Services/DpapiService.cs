#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Provides DPAPI encryption for passwords using Windows Data Protection API.
/// Uses CurrentUser scope so only the current Windows user can decrypt.
/// </summary>
public static class DpapiService
{
    /// <summary>
    /// Encrypts a plaintext string using DPAPI (CurrentUser scope).
    /// Returns a base64-encoded string suitable for JSON storage.
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var encryptedBytes = ProtectedData.Protect(
                plaintextBytes,
                null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DPAPI encryption failed");
            return plaintext; // Fallback: store plaintext (not ideal but won't break)
        }
    }

    /// <summary>
    /// Decrypts a DPAPI-encrypted base64 string.
    /// Returns the plaintext on success, or the input as-is if decryption fails
    /// (handles legacy unencrypted passwords).
    /// </summary>
    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return encryptedBase64;

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            // Not a DPAPI string — assume legacy plaintext password
            Log.Debug("DPAPI decryption failed (legacy password?): {Msg}", ex.Message);
            return encryptedBase64;
        }
    }
}
