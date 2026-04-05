namespace Lumi.Domain.Entities
{
    public class MeetingParticipant
    {
        public int Id { get; set; }
        public int MeetingId { get; set; }
        public Meeting Meeting { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public int? DurationSeconds { get; set; }
        public bool IsPresent { get; set; }
    }
}