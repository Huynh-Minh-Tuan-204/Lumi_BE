namespace Lumi.Domain.Entities
{
    public class UserKey
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public string PublicKey { get; set; }
        public string PrivateKeyEncrypted { get; set; }
        public string PrivateKeyRecoveryEncrypted { get; set; }
        public string RecoveryKeyHash { get; set; }
        public int KeyVersion { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; }
    }
}