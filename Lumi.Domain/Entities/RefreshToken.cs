namespace Lumi.Domain.Entities
{
    public class RefreshToken
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public int DeviceId { get; set; }
        public UserDevice Device { get; set; }
        public string TokenHash { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? RevokedAt { get; set; }

        public int? ReplacedByTokenId { get; set; }
        public RefreshToken ReplacedByToken { get; set; } 
        public RefreshToken ReplacedToken { get; set; }   
    }
}