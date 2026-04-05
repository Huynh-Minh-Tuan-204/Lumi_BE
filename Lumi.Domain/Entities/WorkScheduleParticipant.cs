using System;

namespace Lumi.Domain.Entities
{
    public class WorkScheduleParticipant
    {
        public int Id { get; set; }
        public int WorkScheduleId { get; set; }
        public virtual WorkSchedule WorkSchedule { get; set; }
        
        public int UserId { get; set; }
        public virtual User User { get; set; }

        // Mặc định Pending, có thể là Accepted, Declined
        public string Status { get; set; } = "Pending"; 
        public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }
    }
}
