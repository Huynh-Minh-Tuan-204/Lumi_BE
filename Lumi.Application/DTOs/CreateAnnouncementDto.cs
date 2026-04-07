using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Lumi.Application.DTOs
{
    public class CreateAnnouncementDto
    {
        [Required]
        [StringLength(255)]
        public string Title { get; set; }

        [Required]
        [StringLength(2000)]
        public string Message { get; set; }

        public string Category { get; set; } // "Security", "System", "General"
        public bool ForceConfirmed { get; set; } // Bắt buộc popup cho User
        
        public string IV { get; set; }
        public List<int> UserIds { get; set; }
    }
}
