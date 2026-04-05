namespace Lumi.Application.DTOs
{
    public class TokenResponseDto
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public bool IsFirstLogin { get; set; }
    }
}