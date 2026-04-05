using System.ComponentModel.DataAnnotations;

namespace Lumi.Application.DTOs
{
    public class AddMemberDto
    {
        [Required]
        public int UserId { get; set; }
        public string RoleInConversation { get; set; } = "Member";
    }
}
