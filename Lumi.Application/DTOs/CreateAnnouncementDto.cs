using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Lumi.Application.DTOs
{
    public class CreateAnnouncementDto
    {
        [Required]
        [StringLength(2000)]
        public string Message { get; set; }

        public string IV { get; set; }
        public List<int> UserIds { get; set; }
    }
}
