using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.TiaOpenness.V21;

#pragma warning disable CA1416 // TIA Openness workers and bindings are Windows-only.

internal static class TiaOpennessV21ProtectedCredential
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("PLCSIM.UnitTest.TiaOpenness.V21.SafetyProgram");

    public static string Protect(SecureString credential)
    {
        if (credential == null)
        {
            throw new ArgumentNullException(nameof(credential));
        }

        IntPtr credentialPointer = IntPtr.Zero;
        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        try
        {
            credentialPointer = Marshal.SecureStringToGlobalAllocUnicode(credential);
            plaintext = new byte[checked(credential.Length * sizeof(char))];
            Marshal.Copy(credentialPointer, plaintext, 0, plaintext.Length);
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            if (protectedBytes is not null)
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }

            if (plaintext is not null)
            {
                Array.Clear(plaintext, 0, plaintext.Length);
            }

            if (credentialPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(credentialPointer);
            }
        }
    }

    public static SecureString Unprotect(string protectedCredential)
    {
        if (string.IsNullOrWhiteSpace(protectedCredential))
        {
            throw new ArgumentException("Protected credential payload is required.", nameof(protectedCredential));
        }

        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedCredential);
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (plaintext.Length % sizeof(char) != 0)
            {
                throw new CryptographicException("Protected credential payload has an invalid length.");
            }

            var credential = new SecureString();
            for (var offset = 0; offset < plaintext.Length; offset += sizeof(char))
            {
                credential.AppendChar((char)(plaintext[offset] | plaintext[offset + 1] << 8));
            }

            credential.MakeReadOnly();
            return credential;
        }
        finally
        {
            if (plaintext is not null)
            {
                Array.Clear(plaintext, 0, plaintext.Length);
            }

            if (protectedBytes is not null)
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }
    }
}

#pragma warning restore CA1416
