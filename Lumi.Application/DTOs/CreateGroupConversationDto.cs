using System.Collections.Generic;

namespace Lumi.Application.DTOs
{
    public class CreateGroupConversationDto
    {
        public string Name { get; set; }
        public List<int> MemberIds { get; set; }
    }
}
