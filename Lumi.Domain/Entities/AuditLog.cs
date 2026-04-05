namespace Lumi.Domain.Entities
{
    public class AuditLog
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public string Action { get; set; }
        public string TargetType { get; set; }
        public int? TargetId { get; set; }
        public string PreviousHash { get; set; }
        public string RecordHash { get; set; }
        public DateTime CreatedAt { get; set; }
        public string IpAddress { get; set; }
    }
}