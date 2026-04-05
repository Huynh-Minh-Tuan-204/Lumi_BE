using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Lumi.Application.DTOs
{
    public class CreateGroupDto
    {
        [Required]
        public string Name { get; set; }
        public List<int> MemberIds { get; set; }
    }
}
