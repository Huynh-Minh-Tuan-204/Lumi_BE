using System.ComponentModel.DataAnnotations;
using System;
using System.Collections.Generic;

namespace Lumi.Application.DTOs
{
    public class StartMeetingDto
    {
        [Required]
        public string Title { get; set; }

        public DateTime? ScheduledAt { get; set; }

        public List<int> ParticipantIds { get; set; }
    }
}
