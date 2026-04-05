using Lumi.Application.Interfaces;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Lumi.Infrastructure.Services
{
    public class PasswordService : IPasswordService
    {
        private const int HashSize = 32;
        private const int SaltSize = 16;
        private const int Iterations = 100000;

        public void CreatePasswordHash(string password, out string passwordHash, out string passwordSalt)
        {
            // Sửa lỗi: Chặn null/empty ngay khi tạo hash
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password cannot be null or empty", nameof(password));
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA512,
                HashSize);

            passwordSalt = Convert.ToBase64String(salt);
            passwordHash = Convert.ToBase64String(hash);
        }

        public bool VerifyPassword(string password, string storedHash, string storedSalt)
        {
            // SỬA LỖI: Thêm kiểm tra password đầu vào để tránh Exception
            if (string.IsNullOrEmpty(password) ||
                string.IsNullOrEmpty(storedHash) ||
                string.IsNullOrEmpty(storedSalt))
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(storedSalt);
                byte[] hash = Convert.FromBase64String(storedHash);

                byte[] testHash = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Iterations,
                    HashAlgorithmName.SHA512,
                    HashSize);

                return CryptographicOperations.FixedTimeEquals(hash, testHash);
            }
            catch
            {
                // Phòng trường hợp chuỗi Base64 trong DB bị hỏng không convert được
                return false;
            }
        }
    }
}