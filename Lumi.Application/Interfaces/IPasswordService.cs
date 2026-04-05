namespace Lumi.Application.Interfaces
{
    public interface IPasswordService
    {
        void CreatePasswordHash(string password, out string passwordHash, out string passwordSalt);
        bool VerifyPassword(string password, string storedHash, string storedSalt);
    }
}