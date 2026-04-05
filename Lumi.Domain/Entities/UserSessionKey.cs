namespace Lumi.Domain.Entities
{
    public class UserSessionKey
    {
        public int Id { get; set; }
        public int DeviceId { get; set; }
        public UserDevice Device { get; set; }
        public string SessionPublicKey { get; set; }
        public string SessionPrivateKeyEncrypted { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; }
    }
}