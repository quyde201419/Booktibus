using System.Security.Cryptography;

namespace Booktibus.Helpers
{
    /// <summary>
    /// Provides PBKDF2-SHA256 password hashing and constant-time verification
    /// ready for SQL Server storage without ever storing plaintext passwords.
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSizeBytes = 16;
        private const int HashSizeBytes = 32;
        private const int Iterations = 100_000;

        /// <summary>
        /// Generates a cryptographic random salt and computes the PBKDF2-SHA256 hash.
        /// </summary>
        public static (string HashBase64, string SaltBase64) HashPassword(string password)
        {
            ArgumentException.ThrowIfNullOrEmpty(password);

            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                HashSizeBytes);

            return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
        }

        /// <summary>
        /// Verifies a candidate password against a stored Base64 hash and salt in constant time.
        /// </summary>
        public static bool VerifyPassword(string password, string storedHashBase64, string storedSaltBase64)
        {
            if (string.IsNullOrEmpty(password) ||
                string.IsNullOrWhiteSpace(storedHashBase64) ||
                string.IsNullOrWhiteSpace(storedSaltBase64))
            {
                return false;
            }

            try
            {
                byte[] saltBytes = Convert.FromBase64String(storedSaltBase64);
                byte[] expectedHashBytes = Convert.FromBase64String(storedHashBase64);

                byte[] actualHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    saltBytes,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    expectedHashBytes.Length);

                return CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}

