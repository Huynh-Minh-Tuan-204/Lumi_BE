namespace Lumi.Domain.Entities
{
    public class Announcement
    {
        public int Id { get; set; }

        public string Message { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsDeleted { get; set; } = false;
        public bool IsRead { get; set; } = false;
    }
}