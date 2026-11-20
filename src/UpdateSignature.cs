using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RyvenPanel
{
    /// <summary>
    /// Release signatures (ECDSA P-256 / SHA-256). The private key exists only on the owner's PC, DPAPI-protected in
    /// %APPDATA%\PXMX-ServerSuite\release-signing.key. Signed message:
    /// "PXMX-ServerSuite|&lt;version&gt;|&lt;sha256&gt;|&lt;size&gt;", signature = base64 (IEEE P1363 r||s) in version.json field "signatur".
    /// Whoever controls the server alone can therefore no longer push a different exe. See docs/SIGNIERTE-UPDATES.md.
    /// </summary>
    static class UpdateSignature
    {
        /// <summary>BCRYPT_ECCPUBLIC_BLOB (ECS1) of the release key, base64.</summary>
        public const string PublicKey = "RUNTMSAAAADbczvszk5Iwc4Km69L+zM4ZengN0zu2YW1tSII+MMdlSz0tA1ov2s67hYtlnlQxhWn3hm0ZcSO19AvYoJJ79iX";

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PXMX-ServerSuite-release-signing-v1");

        public static string KeyFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite", "release-signing.key"); }
        }

        public static byte[] Message(string version, string sha256, long size)
        {
            return Encoding.UTF8.GetBytes("PXMX-ServerSuite|" + (version ?? "").Trim() + "|" + (sha256 ?? "").Trim().ToLowerInvariant() + "|" + size);
        }

        public static bool Verify(string version, string sha256, long size, string signatureB64)
        {
            return Verify(version, sha256, size, signatureB64, PublicKey);
        }

        public static bool Verify(string version, string sha256, long size, string signatureB64, string publicKeyB64)
        {
            if (string.IsNullOrWhiteSpace(signatureB64) || string.IsNullOrWhiteSpace(sha256) || size <= 0) return false;
            try
            {
                var sig = Convert.FromBase64String(signatureB64.Trim());
                if (sig.Length != 64) return false;
                using (var key = CngKey.Import(Convert.FromBase64String(publicKeyB64), CngKeyBlobFormat.EccPublicBlob))
                using (var ec = new ECDsaCng(key))
                {
                    ec.HashAlgorithm = CngAlgorithm.Sha256;
                    return ec.VerifyData(Message(version, sha256, size), sig);
                }
            }
            catch { return false; }
        }

        public static bool CanSign { get { return File.Exists(KeyFile); } }

        /// <summary>Signs with the local DPAPI-protected key; throws with a German message if it is missing or does not match.</summary>
        public static string Sign(string version, string sha256, long size)
        {
            if (!File.Exists(KeyFile))
                throw new Exception("Kein Signaturschlüssel auf diesem PC (" + KeyFile + ").\nUpdates können nur vom Release-PC des Inhabers signiert und verteilt werden – siehe docs/SIGNIERTE-UPDATES.md.");
            byte[] blob = null;
            try
            {
                blob = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser);
                string sig;
                using (var key = CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob))
                using (var ec = new ECDsaCng(key))
                {
                    ec.HashAlgorithm = CngAlgorithm.Sha256;
                    sig = Convert.ToBase64String(ec.SignData(Message(version, sha256, size)));
                }
                if (!Verify(version, sha256, size, sig))
                    throw new Exception("Der lokale Signaturschlüssel passt nicht zum öffentlichen Schlüssel dieser Version – Update wird nicht verteilt.");
                return sig;
            }
            catch (CryptographicException ex)
            {
                throw new Exception("Signaturschlüssel konnte nicht entschlüsselt werden (falscher Windows-Benutzer?): " + ex.Message);
            }
            finally { if (blob != null) Array.Clear(blob, 0, blob.Length); }
        }
    }
}
