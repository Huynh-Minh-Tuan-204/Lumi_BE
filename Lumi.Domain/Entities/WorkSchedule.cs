using System;
using System.Collections.Generic;

namespace Lumi.Domain.Entities
{
    public class WorkSchedule
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string Location { get; set; }
        
        public int CreatedBy { get; set; }
        public virtual User Creator { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public virtual ICollection<WorkScheduleParticipant> Participants { get; set; }
    }
}
